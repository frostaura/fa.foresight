"""
v2 features for BTC 5-min direction: base + microstructure + cross-asset + cross-interval.
All leakage-safe: every feature at bar t uses only data with timestamp <= close of bar t.
Target y[t] = 1 if close[t+1] > close[t].
"""
import numpy as np
import pandas as pd
from features import build_features  # v1 base (40 leakage-safe BTC features + y + fwd_ret)

ASSETS = ["ETHUSDT", "SOLUSDT", "BNBUSDT"]


_FILE = {"ETHUSDT": "ETHclean"}  # use the re-downloaded clean ETH file


def _load(sym):
    return pd.read_csv(f"{_FILE.get(sym, sym)}_5m.csv")


def add_microstructure(out, df):
    o, h, l, c = df["open"], df["high"], df["low"], df["close"]
    vol, qvol, trades, tb = df["volume"], df["quote_vol"], df["trades"].astype(float), df["taker_base"]
    r1 = c.pct_change()
    # Order-flow imbalance (taker buy vs total), known at bar close
    out["ofi"] = (2 * tb - vol) / (vol + 1e-12)
    out["ofi_ema12"] = out["ofi"].ewm(span=12).mean()
    signed_vol = (2 * tb - vol)
    out["signed_vol_z24"] = (signed_vol - signed_vol.rolling(24).mean()) / (signed_vol.rolling(24).std() + 1e-12)
    # Kyle's lambda proxy: price impact per unit signed flow (rolling)
    out["kyle_lambda"] = (r1.abs().rolling(24).mean()) / (signed_vol.abs().rolling(24).mean() + 1e-12)
    # Amihud illiquidity: |return| per unit quote volume
    out["amihud"] = (r1.abs() / (qvol + 1e-12)).ewm(span=24).mean()
    # Roll effective-spread estimator: 2*sqrt(-cov(dp_t, dp_{t-1}))
    dp = c.diff()
    cov = dp.rolling(48).cov(dp.shift(1))
    out["roll_spread"] = 2 * np.sqrt(np.clip(-cov, 0, None)) / c
    # Garman-Klass volatility (uses OHLC of the closed bar)
    gk = 0.5 * (np.log(h / l)) ** 2 - (2 * np.log(2) - 1) * (np.log(c / o)) ** 2
    out["gk_vol"] = np.sqrt(np.clip(gk, 0, None))
    out["gk_vol_12"] = out["gk_vol"].rolling(12).mean()
    # Trade intensity
    out["trade_intensity"] = trades / (trades.ewm(span=24).mean() + 1e-12)
    return out


def add_cross_asset(out, btc, alt_dfs):
    """Contemporaneous alt info at bar t is known at the shared 5m close -> safe to predict t+1."""
    btc_r = btc["close"].pct_change()
    alt_r = {}
    for sym, a in alt_dfs.items():
        m = btc[["open_time"]].merge(a[["open_time", "close"]], on="open_time", how="left", suffixes=("", f"_{sym}"))
        m["close"] = m["close"].ffill()                      # gap-robust: never propagate NaN through rolling stats
        ar = m["close"].pct_change(fill_method=None)
        alt_r[sym] = ar.values
        tag = sym.replace("USDT", "").lower()
        out[f"ret_{tag}_1"] = ar.values
        out[f"ret_{tag}_3"] = m["close"].pct_change(3, fill_method=None).values
        # rolling correlation & beta of BTC on this alt (regime conditioner)
        s_btc = pd.Series(btc_r.values); s_alt = pd.Series(ar.values)
        out[f"corr_{tag}_96"] = s_btc.rolling(96).corr(s_alt).values
        cov = s_btc.rolling(96).cov(s_alt)
        out[f"beta_{tag}_96"] = (cov / (s_alt.rolling(96).var() + 1e-12)).values
    # aggregate breadth / dispersion / relative strength (all from bar-t alt returns)
    A = np.column_stack([alt_r[s] for s in alt_dfs])
    out["alt_breadth"] = np.nanmean((A > 0).astype(float), axis=1)
    out["alt_dispersion"] = np.nanstd(A, axis=1)
    out["rel_strength_btc"] = btc_r.values - np.nanmean(A, axis=1)
    # lead-lag: does ETH's PREVIOUS bar move predict BTC? (lagged alt return)
    out["eth_lead_1"] = pd.Series(alt_r["ETHUSDT"]).shift(1).values
    return out


def add_cross_interval(out, btc):
    """Higher-timeframe regime aligned with NO look-ahead: at 5m close C_t attach the most
    recently CLOSED HTF bar (htf_end <= C_t) via backward merge_asof."""
    d = btc.copy()
    d["dt_open"] = pd.to_datetime(d["open_time"], unit="ms", utc=True)
    d["close_dt"] = d["dt_open"] + pd.Timedelta(minutes=5)
    base = d[["close_dt"]].copy()
    for tf, rule in [("15m", "15min"), ("1h", "1h"), ("4h", "4h")]:
        g = (d.set_index("dt_open")
               .resample(rule, label="left", closed="left")
               .agg(open=("open", "first"), high=("high", "max"), low=("low", "min"),
                    close=("close", "last")).dropna())
        cc = g["close"]
        delta = cc.diff()
        gain = delta.clip(lower=0).rolling(14).mean(); loss = (-delta.clip(upper=0)).rolling(14).mean()
        rsi = 100 - 100 / (1 + gain / (loss + 1e-12))
        ema_f = cc.ewm(span=10).mean(); ema_s = cc.ewm(span=30).mean()
        roll_min = g["low"].rolling(24).min(); roll_max = g["high"].rolling(24).max()
        htf = pd.DataFrame({
            f"{tf}_ret1": cc.pct_change(),
            f"{tf}_ret4": cc.pct_change(4),
            f"{tf}_rsi": rsi,
            f"{tf}_trend": (ema_f - ema_s) / cc,
            f"{tf}_posrange": (cc - roll_min) / (roll_max - roll_min + 1e-12),
            f"{tf}_rvol": cc.pct_change().rolling(24).std(),
        })
        # the bar covering [start, start+tf) is only KNOWN at its end -> key = end timestamp
        htf["key"] = htf.index + pd.Timedelta(rule)
        htf = htf.dropna().sort_values("key").reset_index(drop=True)
        merged = pd.merge_asof(base.sort_values("close_dt"), htf,
                               left_on="close_dt", right_on="key", direction="backward")
        for col in htf.columns:
            if col != "key":
                out[col] = merged[col].values
    return out


def build_v2():
    btc = _load("btc_5m") if False else pd.read_csv("btc_5m.csv")
    alt_dfs = {s: _load(s) for s in ASSETS}
    out = build_features(btc)                      # v1 base (has y, fwd_ret, open_time, close)
    out = add_microstructure(out, btc)
    out = add_cross_asset(out, btc, alt_dfs)
    out = add_cross_interval(out, btc)
    # clean inf
    feat_cols = [c for c in out.columns if c not in ("y", "fwd_ret", "open_time", "close")]
    out[feat_cols] = out[feat_cols].replace([np.inf, -np.inf], np.nan)
    return out, feat_cols


if __name__ == "__main__":
    out, cols = build_v2()
    clean = out.dropna().reset_index(drop=True)
    print("rows raw:", len(out), " usable:", len(clean), " features:", len(cols))
    print("target balance:", round(clean["y"].mean(), 4))
    print("NaN in usable:", int(clean[cols].isna().sum().sum()), " inf:", int(np.isinf(clean[cols].values).sum()))
    new = [c for c in cols if c not in __import__("features").feature_columns(__import__("features").build_features(pd.read_csv("btc_5m.csv")))]
    print("NEW feature count:", len(new))
    print("sample NEW:", new[:25])
    out.to_pickle("btc_v2_features.pkl")
    print("saved btc_v2_features.pkl")
