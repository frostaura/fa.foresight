"""
Leakage-safe feature engineering for BTC 5-minute direction prediction.

Convention (critical for no look-ahead):
  - A row t represents the bar that OPENED at open_time[t] and has fully CLOSED.
  - Every feature at row t uses ONLY information available at the close of bar t
    (i.e. data from rows <= t). All rolling windows are backward-looking.
  - The TARGET y[t] = 1 if close[t+1] > close[t] else 0  (direction of the NEXT bar).
  - We therefore predict the future using only the past. The last row has no
    known target and is dropped.
"""
import numpy as np
import pandas as pd


def build_features(df: pd.DataFrame) -> pd.DataFrame:
    df = df.copy().reset_index(drop=True)
    o, h, l, c = df["open"], df["high"], df["low"], df["close"]
    vol, qvol, trades = df["volume"], df["quote_vol"], df["trades"]
    taker_base = df["taker_base"]

    ret1 = c.pct_change()  # return realised over bar t (known at close of t)
    out = pd.DataFrame(index=df.index)

    # --- Multi-horizon momentum (past returns, known at t) ---
    for k in [1, 2, 3, 6, 12, 24, 48]:
        out[f"ret_{k}"] = c.pct_change(k)

    # --- Short-horizon momentum / acceleration ---
    out["mom_3_12"] = c.pct_change(3) - c.pct_change(12)
    out["mom_6_24"] = c.pct_change(6) - c.pct_change(24)
    out["accel"] = ret1 - ret1.shift(1)

    # --- Volatility regime (backward-looking std of 1-bar returns) ---
    for w in [6, 12, 24, 48]:
        out[f"vol_{w}"] = ret1.rolling(w).std()
    out["vol_ratio_12_48"] = out["vol_12"] / (out["vol_48"] + 1e-12)

    # --- Candle geometry / microstructure (current bar, known at close) ---
    rng = (h - l) / c
    body = (c - o) / c
    upper_wick = (h - np.maximum(o, c)) / c
    lower_wick = (np.minimum(o, c) - l) / c
    out["range"] = rng
    out["body"] = body
    out["upper_wick"] = upper_wick
    out["lower_wick"] = lower_wick
    out["body_to_range"] = body / (rng + 1e-12)
    out["wick_skew"] = (upper_wick - lower_wick) / (rng + 1e-12)
    out["range_z_24"] = (rng - rng.rolling(24).mean()) / (rng.rolling(24).std() + 1e-12)

    # --- Position within recent range (mean-reversion pressure) ---
    for w in [12, 24, 48]:
        roll_min = l.rolling(w).min()
        roll_max = h.rolling(w).max()
        out[f"pctl_{w}"] = (c - roll_min) / (roll_max - roll_min + 1e-12)  # 0..1
        sma = c.rolling(w).mean()
        sd = c.rolling(w).std()
        out[f"z_sma_{w}"] = (c - sma) / (sd + 1e-12)

    # --- RSI(14) ---
    delta = c.diff()
    gain = delta.clip(lower=0).rolling(14).mean()
    loss = (-delta.clip(upper=0)).rolling(14).mean()
    rs = gain / (loss + 1e-12)
    out["rsi_14"] = 100 - 100 / (1 + rs)

    # --- Volume / flow dynamics (z-scores vs recent history) ---
    for col, name in [(vol, "vol"), (qvol, "qvol"), (trades.astype(float), "trades")]:
        m = col.rolling(24).mean()
        s = col.rolling(24).std()
        out[f"{name}_z_24"] = (col - m) / (s + 1e-12)
    out["taker_buy_ratio"] = taker_base / (vol + 1e-12)          # 0..1, buy pressure
    out["taker_buy_ratio_dev"] = out["taker_buy_ratio"] - out["taker_buy_ratio"].rolling(24).mean()
    out["dvol_1"] = vol.pct_change().clip(-5, 5)

    # --- Signed-volume momentum (return * relative volume) ---
    out["signed_flow_6"] = (np.sign(ret1) * (vol / (vol.rolling(24).mean() + 1e-12))).rolling(6).mean()

    # --- Time-of-day / week seasonality (UTC) ---
    dt = pd.to_datetime(df["open_time"], unit="ms", utc=True)
    hour = dt.dt.hour + dt.dt.minute / 60.0
    dow = dt.dt.dayofweek
    out["hour_sin"] = np.sin(2 * np.pi * hour / 24)
    out["hour_cos"] = np.cos(2 * np.pi * hour / 24)
    out["dow_sin"] = np.sin(2 * np.pi * dow / 7)
    out["dow_cos"] = np.cos(2 * np.pi * dow / 7)

    # --- TARGET: direction of the NEXT bar (the only forward-looking column) ---
    fwd_ret = c.shift(-1) / c - 1.0
    out["y"] = (fwd_ret > 0).astype(float)
    out["fwd_ret"] = fwd_ret           # kept for cost analysis, NOT a feature
    out["open_time"] = df["open_time"]
    out["close"] = c
    return out


FEATURE_COLS_EXCLUDE = {"y", "fwd_ret", "open_time", "close"}


def feature_columns(feat: pd.DataFrame):
    return [c for c in feat.columns if c not in FEATURE_COLS_EXCLUDE]


def load_raw():
    """Load the raw 5-min bars. Prefers the fast pickle, falls back to the shipped CSV."""
    import os
    if os.path.exists("btc_5m.pkl"):
        return pd.read_pickle("btc_5m.pkl")
    return pd.read_csv("btc_5m.csv")


if __name__ == "__main__":
    df = load_raw()
    feat = build_features(df)
    cols = feature_columns(feat)
    # Drop warmup rows (NaN from rolling) and the last row (no target)
    clean = feat.dropna().reset_index(drop=True)
    print("raw rows:", len(feat), " usable rows:", len(clean), " features:", len(cols))
    print("feature list:", cols)
    print("NaN remaining:", int(clean[cols].isna().sum().sum()))
    print("inf remaining:", int(np.isinf(clean[cols].values).sum()))
    print("target balance:", round(clean["y"].mean(), 4))
    feat.to_pickle("btc_5m_features.pkl")
    print("saved btc_5m_features.pkl")
