"""v5 = v4 (spot+cross-asset+cross-interval+microstructure+cross-exchange+derivatives)
      + broad altcoin cross-section (14 coins) + open interest / positioning.
All new feeds are keyed by open_time and already leakage-safe (built by subagents)."""
import numpy as np, pandas as pd

v4 = pd.read_pickle("btc_v4_features.pkl")
alt = pd.read_csv("broad_alt_features.csv").rename(
    columns=lambda x: ("ba_" + x) if x != "open_time" else x)   # avoid clash with v4's alt_* cols
oi = pd.read_csv("oi_features.csv").drop(columns=["oi_value"], errors="ignore")  # drop raw level dup

out = v4.merge(alt, on="open_time", how="left").merge(oi, on="open_time", how="left")
new_cols = [c for c in list(alt.columns) + list(oi.columns) if c != "open_time"]
out[new_cols] = out[new_cols].replace([np.inf, -np.inf], np.nan)
out.to_pickle("btc_v5_features.pkl")
clean = out.dropna().reset_index(drop=True)
nfeat = len([c for c in out.columns if c not in ("y", "fwd_ret", "open_time", "close")])
print(f"v5 rows usable={len(clean)} total_features={nfeat} new={len(new_cols)}")
print("new:", new_cols)
