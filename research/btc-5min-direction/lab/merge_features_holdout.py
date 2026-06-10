#!/usr/bin/env python3
"""Merge the three family parquets onto the BTC 5m grid -> features_holdout.pkl.

Deterministic. Grid = spot_BTCUSDT_5m.csv open_time < 2026-06-01T00:00Z.
Left-joins price_flow, derivatives, cross_asset on open_time; any duplicate
feature name across families would be prefixed "<family>__" (none in v1).
Also merges the three per-family manifests into features/manifest.json.

Usage: python3 merge_features_v1.py
"""
import json
import os
from collections import Counter

import numpy as np
import pandas as pd

LAB = os.path.dirname(os.path.abspath(__file__))
HOLDOUT_START_MS = 1781136000000  # 2026-06-11T00:00Z (holdout build)
FAMILIES = ["price_flow", "derivatives", "cross_asset"]
NAN_REPORT_AFTER_ROW = 5000
NAN_DROP_PCT = 5.0

def main():
    # ---- grid ----
    spot = pd.read_csv(os.path.join(LAB, "data", "spot_BTCUSDT_5m.csv"),
                       usecols=["open_time", "close"])
    spot = spot[spot["open_time"] < HOLDOUT_START_MS].reset_index(drop=True)
    grid = pd.DataFrame({"open_time": spot["open_time"].astype(np.int64),
                         "close": spot["close"].astype(np.float64)})
    assert grid["open_time"].is_monotonic_increasing
    assert not grid["open_time"].duplicated().any()
    # exact 5m grid check
    assert (np.diff(grid["open_time"].values) == 300000).all(), "grid not contiguous 5m"
    print(f"grid rows: {len(grid)}  span "
          f"{pd.to_datetime(grid.open_time.iloc[0], unit='ms')} .. "
          f"{pd.to_datetime(grid.open_time.iloc[-1], unit='ms')}")

    # ---- duplicate-name resolution plan ----
    fam_cols = {}
    for fam in FAMILIES:
        df = pd.read_parquet(os.path.join(LAB, "features", f"{fam}.parquet"))
        fam_cols[fam] = df
    name_counts = Counter(c for df in fam_cols.values()
                          for c in df.columns if c != "open_time")
    dup_names = {c for c, n in name_counts.items() if n > 1}
    if dup_names:
        print("duplicate feature names, prefixing with family:", sorted(dup_names))

    # ---- merge manifests ----
    manifest = {}
    merged = grid
    for fam in FAMILIES:
        df = fam_cols[fam]
        with open(os.path.join(LAB, "features", f"{fam}_manifest.json")) as f:
            fam_man = json.load(f)
        ren = {c: f"{fam}__{c}" for c in df.columns
               if c != "open_time" and c in dup_names}
        if ren:
            df = df.rename(columns=ren)
            fam_man = {ren.get(k, k): v for k, v in fam_man.items()}
        feat_cols = [c for c in df.columns if c != "open_time"]
        df[feat_cols] = df[feat_cols].astype(np.float64)
        merged = merged.merge(df, on="open_time", how="left", validate="1:1")
        for k in feat_cols:
            manifest[k] = fam_man.get(k, "(no manifest entry)")

    assert len(merged) == len(grid)

    # ---- sanity: inf, NaN after row 5000 ----
    feats = [c for c in merged.columns if c not in ("open_time", "close")]
    inf_cols = [c for c in feats if np.isinf(merged[c].values).any()]
    assert not inf_cols, f"inf in columns: {inf_cols}"
    tail = merged.iloc[NAN_REPORT_AFTER_ROW:]
    nan_pct = tail[feats].isna().mean() * 100
    bad = nan_pct[nan_pct > NAN_DROP_PCT].sort_values(ascending=False)
    print(f"\ncolumns >{NAN_DROP_PCT}% NaN after row {NAN_REPORT_AFTER_ROW}: {len(bad)}")
    dropped = []
    if len(bad):
        print(bad.to_string())
        dropped = list(bad.index)
        merged = merged.drop(columns=dropped)
        for c in dropped:
            manifest.pop(c, None)
        feats = [c for c in feats if c not in dropped]
        print("DROPPED:", dropped)
    worst = nan_pct.drop(index=dropped).sort_values(ascending=False).head(8)
    print("worst remaining NaN% after row 5000:")
    print(worst.round(3).to_string())

    assert merged["open_time"].is_monotonic_increasing
    assert int(merged["open_time"].max()) < HOLDOUT_START_MS

    out_pkl = os.path.join(LAB, "features_holdout.pkl")
    merged.to_pickle(out_pkl)
    with open(os.path.join(LAB, "features", "manifest_holdout_unused.json"), "w") as f:
        json.dump(manifest, f, indent=1)
    print(f"\nwrote {out_pkl}  shape={merged.shape}  "
          f"({len(feats)} features + open_time + close)")
    print(f"wrote features/manifest.json ({len(manifest)} entries)")
    if dropped:
        print("dropped columns recorded above")

if __name__ == "__main__":
    main()
