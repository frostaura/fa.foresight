#!/usr/bin/env python3
"""
build_derivatives.py -- derivatives family: positioning, leverage, forced-flow proxies.

Sources: metrics_5m (OI + L/S ratios), funding_8h, premium_idx_5m, perp vs spot basis,
deribit_funding_8h. Deterministic; no randomness.

LEAKAGE NOTES
- Grid = BTC spot 5m open_time < --end. Feature at bar t uses only info available by t+5m.
- spot/perp/premium 5m: same-open_time join (their bar t closes at t+5m -- legal).
- metrics_5m: row timestamp is the observation moment -> merge_asof backward, <= t.
  After the asof join, per-column NaNs (short upstream gaps, max run 30 rows) are
  ffilled with limit=48 (4h) and documented; no mid-series NaN blocks remain.
- funding_8h / deribit_funding_8h: known at funding_time -> merge_asof backward <= t.
  (Some funding_time stamps carry ms jitter, e.g. ...00.011; backward asof handles it
  conservatively -- the record becomes visible one bar later.)
- minutes-to-NEXT-funding is pure calendar (00/08/16 UTC) -- legal forward info.
- All rolling stats (z, percentile, median, mean) use trailing windows only.
"""
import argparse
import json
import os

import numpy as np
import pandas as pd

HERE = os.path.dirname(os.path.abspath(__file__))
DATA = os.path.join(HERE, "..", "data")

BAR_MS = 5 * 60 * 1000
D30 = 288 * 30   # 8640 bars = 30 days
D1 = 288         # 24h


def zscore(s: pd.Series, window: int, min_periods: int) -> pd.Series:
    m = s.rolling(window, min_periods=min_periods).mean()
    sd = s.rolling(window, min_periods=min_periods).std()
    return (s - m) / sd.replace(0.0, np.nan)


def pctile(s: pd.Series, window: int, min_periods: int) -> pd.Series:
    return s.rolling(window, min_periods=min_periods).rank(pct=True)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--end", default="2026-06-01")
    args = ap.parse_args()
    end_ms = int(pd.Timestamp(args.end, tz="UTC").timestamp() * 1000)

    # ---------------- grid + price ----------------
    spot = pd.read_csv(os.path.join(DATA, "spot_BTCUSDT_5m.csv"),
                       usecols=["open_time", "high", "low", "close"])
    spot = spot[spot.open_time < end_ms].sort_values("open_time").reset_index(drop=True)
    out = pd.DataFrame({"open_time": spot.open_time.astype("int64")})
    close = spot.close

    perp = pd.read_csv(os.path.join(DATA, "perp_BTCUSDT_5m.csv"),
                       usecols=["open_time", "close"]).rename(columns={"close": "perp_close"})
    prem = pd.read_csv(os.path.join(DATA, "premium_idx_5m.csv"),
                       usecols=["open_time", "close"]).rename(columns={"close": "prem_close"})
    g = spot.merge(perp, on="open_time", how="left").merge(prem, on="open_time", how="left")
    # premium/perp share the validated 5m grid; guard tiny gaps just in case
    g[["perp_close", "prem_close"]] = g[["perp_close", "prem_close"]].ffill(limit=12)

    # ---------------- metrics (as-of <= t) ----------------
    met = pd.read_csv(os.path.join(DATA, "metrics_5m.csv")).sort_values("open_time")
    met = met[met.open_time < end_ms + BAR_MS]  # harmless trim; asof enforces <= t anyway
    # upstream outage 2025-01-05 17:30-21:25 UTC records OI as 0.0 -> treat as missing
    met.loc[met.sum_open_interest <= 0, "sum_open_interest"] = np.nan
    g = pd.merge_asof(g, met, on="open_time", direction="backward",
                      allow_exact_matches=True)
    mcols = ["sum_open_interest", "count_toptrader_long_short_ratio",
             "sum_toptrader_long_short_ratio", "count_long_short_ratio",
             "sum_taker_long_short_vol_ratio"]
    g[mcols] = g[mcols].ffill(limit=48)  # upstream NaN runs <= 30 bars

    oi = g.sum_open_interest

    # ---------------- price context (trailing only) ----------------
    ret_1h = close / close.shift(12) - 1.0
    abs_ret_1h = ret_1h.abs()
    abs_ret_med30 = abs_ret_1h.rolling(D30, min_periods=2880).median()
    range_1h = (spot.high.rolling(12).max() - spot.low.rolling(12).min()) / close
    range_1h_pct = pctile(range_1h, D30, 2880)
    range_1b = (spot.high - spot.low) / close
    range_1b_z = zscore(range_1b, D30, 2880)

    # ---------------- OI dynamics ----------------
    oi_mean_24h = oi.rolling(D1, min_periods=144).mean()
    for k in (1, 4, 12, 48, 288):
        d = (oi - oi.shift(k)) / oi_mean_24h
        out[f"doi_{k}b_z"] = zscore(d, D30, 2880)
    out["oi_fuel"] = oi / oi_mean_24h - 1.0
    out["oi_fuel_z"] = zscore(out.oi_fuel, D30, 2880)
    out["oi_pctile_30d"] = pctile(oi, D30, 2880)

    doi_1h_z = out["doi_12b_z"]

    # ---------------- OI x price interactions (forced-flow core) ----------------
    out["deleverage_dir"] = doi_1h_z * np.sign(ret_1h)
    flat = (abs_ret_1h < abs_ret_med30).astype(float)
    out["oi_flush_flag"] = ((doi_1h_z < -2.0).astype(float) * flat)
    out["oi_spike_flat"] = ((doi_1h_z > 2.0).astype(float) * flat)
    casc = ((range_1h_pct > 0.95) & (doi_1h_z < -2.0)).astype(float) * np.sign(ret_1h)
    out["cascade_signed"] = casc
    casc_recent = casc.abs().rolling(6, min_periods=1).max()
    out["cascade_decel"] = (casc_recent * (range_1b_z.diff() < 0).astype(float))

    # ---------------- positioning ----------------
    ratios = {
        "tt_pos_ls": "sum_toptrader_long_short_ratio",
        "tt_acct_ls": "count_toptrader_long_short_ratio",
        "global_ls": "count_long_short_ratio",
        "taker_ls": "sum_taker_long_short_vol_ratio",
    }
    for name, col in ratios.items():
        s = g[col]
        out[f"{name}_z"] = zscore(s, D30, 2880)
        out[f"{name}_chg4h_z"] = zscore(s - s.shift(48), D30, 2880)
    out["taker_ls_z_ema"] = out.taker_ls_z.ewm(span=48, min_periods=24).mean()
    out["ls_divergence"] = out.tt_pos_ls_z - out.global_ls_z

    # ---------------- funding ----------------
    fund = pd.read_csv(os.path.join(DATA, "funding_8h.csv")).sort_values("funding_time")
    fr = fund.funding_rate
    fund["funding_z90"] = zscore(fr, 270, 30)        # 90d of 8h settlements
    fund["funding_pct90"] = pctile(fr, 270, 30)
    fund["funding_z30"] = zscore(fr, 90, 21)         # 30d, for crowding score
    fund = fund.rename(columns={"funding_time": "open_time"})
    fcols = ["funding_rate", "funding_z90", "funding_pct90", "funding_z30"]
    g = pd.merge_asof(g, fund[["open_time"] + fcols], on="open_time",
                      direction="backward", allow_exact_matches=True)
    out["funding_last_z"] = g.funding_z90
    out["funding_pctile_90d"] = g.funding_pct90

    pred_f = g.prem_close.rolling(96, min_periods=48).mean()   # trailing 8h premium mean
    out["pred_funding_bps"] = pred_f * 1e4
    out["pred_funding_z"] = zscore(pred_f, D30, 2880)

    mod = (out.open_time // 60000) % 480              # minutes since last 8h boundary
    mins_to = (480 - mod).astype(float)               # (0, 480]
    out["mins_to_funding"] = mins_to
    ang = 2.0 * np.pi * mod / 480.0
    out["funding_cycle_sin"] = np.sin(ang)
    out["funding_cycle_cos"] = np.cos(ang)
    prewin = (mins_to <= 60).astype(float)
    extreme = (out.pred_funding_z.abs() > 1.5).astype(float)
    out["prefund_extreme"] = prewin * extreme * np.sign(out.pred_funding_z)
    out["crowding_score"] = -g.funding_z30 * out.oi_fuel_z

    # ---------------- premium / basis ----------------
    out["prem_bps"] = g.prem_close * 1e4
    out["prem_z96"] = zscore(g.prem_close, 96, 48)
    out["prem_chg12_bps"] = (g.prem_close - g.prem_close.shift(12)) * 1e4
    basis = g.perp_close / g.close - 1.0
    out["basis_bps"] = basis * 1e4
    out["basis_z288"] = zscore(basis, 288, 144)
    out["basis_chg12_z"] = zscore(basis - basis.shift(12), D30, 2880)

    # ---------------- deribit funding spread ----------------
    dx = pd.read_csv(os.path.join(DATA, "deribit_funding_8h.csv"),
                     usecols=["funding_time", "interest_8h"]).sort_values("funding_time")
    dx = dx.rename(columns={"funding_time": "open_time"})
    g = pd.merge_asof(g, dx, on="open_time", direction="backward",
                      allow_exact_matches=True)
    spread = g.interest_8h - g.funding_rate
    out["deribit_fund_spread_bps"] = spread * 1e4
    out["deribit_fund_spread_z"] = zscore(spread, D30, 2880)

    # ---------------- finalize ----------------
    feat_cols = [c for c in out.columns if c != "open_time"]
    out[feat_cols] = out[feat_cols].astype("float64")
    out = out.replace([np.inf, -np.inf], np.nan)
    out["open_time"] = out.open_time.astype("int64")

    path = os.path.join(HERE, "derivatives.parquet")
    out.to_parquet(path, index=False)

    # ---------------- validation report ----------------
    n = len(out)
    print(f"rows={n} cols={len(feat_cols)} end_ms={end_ms}")
    assert out.open_time.is_monotonic_increasing and (out.open_time < end_ms).all()
    assert not np.isinf(out[feat_cols].to_numpy()).any()
    tail = out.iloc[5000:]
    bad = []
    for c in feat_cols:
        pall = out[c].isna().mean() * 100
        p5k = tail[c].isna().mean() * 100
        flag = " <-- >5% after bar 5000" if p5k > 5 else ""
        print(f"{c:28s} NaN all={pall:6.2f}%  after5000={p5k:6.2f}%{flag}")
        if p5k > 5:
            bad.append(c)
    print("BAD:", bad if bad else "none")


if __name__ == "__main__":
    main()
