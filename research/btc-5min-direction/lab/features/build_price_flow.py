#!/usr/bin/env python3
"""
build_price_flow.py -- price_flow feature family for BTC 5m direction lab.

BTC's own tape: spot 5m + spot 1m + perp 5m. Deterministic.

Leakage law compliance:
- Feature at open_time=t uses only info available by t+5m (bar t's close moment).
- spot/perp 5m: same-open_time join (their bar t also closes at t+5m).
- 1m sub-bars with open_time in [t, t+5m) all close by t+5m -> legal to aggregate
  into bar t's feature row.
- All rolling/ewm windows are trailing (end at bar t inclusive). No shift(-n),
  no centered windows anywhere.
- All features are returns / ratios / z-scores / percentile ranks -- no raw levels.

Usage: python3 build_price_flow.py --end 2026-06-01
Output: price_flow.parquet (open_time int64 ms + float64 features)
        price_flow_manifest.json
"""
import argparse
import json
import os

import numpy as np
import pandas as pd

HERE = os.path.dirname(os.path.abspath(__file__))
LAB = os.path.dirname(HERE)
DATA = os.path.join(LAB, "data")
EPS = 1e-12


def zscore(s: pd.Series, w: int) -> pd.Series:
    """Trailing rolling z-score."""
    m = s.rolling(w, min_periods=w).mean()
    sd = s.rolling(w, min_periods=w).std()
    return (s - m) / (sd + EPS)


def build(end: str) -> tuple[pd.DataFrame, dict]:
    end_ms = int(pd.Timestamp(end, tz="UTC").timestamp() * 1000)

    spot = pd.read_csv(os.path.join(DATA, "spot_BTCUSDT_5m.csv"))
    spot = spot[spot["open_time"] < end_ms].sort_values("open_time").reset_index(drop=True)
    perp = pd.read_csv(os.path.join(DATA, "perp_BTCUSDT_5m.csv"))
    perp = perp[perp["open_time"] < end_ms].sort_values("open_time").reset_index(drop=True)
    m1 = pd.read_csv(os.path.join(DATA, "spot_BTCUSDT_1m.csv"))
    m1 = m1[m1["open_time"] < end_ms].sort_values("open_time").reset_index(drop=True)

    grid = spot["open_time"].to_numpy()
    out = pd.DataFrame({"open_time": grid.astype(np.int64)})
    man = {}

    o, h, l, c = spot["open"], spot["high"], spot["low"], spot["close"]
    vol, qvol, trades, taker = spot["volume"], spot["quote_vol"], spot["trades"], spot["taker_base"]
    logc = np.log(c)
    r1 = logc.diff()

    # ---------------- realized vol (needed early for normalization) ----------
    rv = {}
    for w in (12, 48, 288):
        rv[w] = r1.rolling(w, min_periods=w).std()
        out[f"rv_cc_{w}"] = rv[w]
        man[f"rv_cc_{w}"] = f"Realized vol: trailing std of 1-bar log returns, {w}-bar window."

    # Garman-Klass per-bar variance
    log_hl = np.log(h / l.replace(0, np.nan))
    log_co = np.log(c / o.replace(0, np.nan))
    gk_var = 0.5 * log_hl**2 - (2 * np.log(2) - 1) * log_co**2
    gk_var = gk_var.clip(lower=0)
    for w in (12, 48, 288):
        out[f"rv_gk_{w}"] = np.sqrt(gk_var.rolling(w, min_periods=w).mean())
        man[f"rv_gk_{w}"] = f"Garman-Klass realized vol, {w}-bar trailing mean of per-bar GK variance, sqrt."

    out["vov_48"] = rv[12].rolling(48, min_periods=48).std() / (
        rv[12].rolling(48, min_periods=48).mean() + EPS)
    man["vov_48"] = "Vol-of-vol: 48-bar std of rv_cc_12 divided by its 48-bar mean (coefficient of variation)."

    W30D = 288 * 30
    out["rv48_prank_30d"] = rv[48].rolling(W30D, min_periods=288 * 5).rank(pct=True)
    man["rv48_prank_30d"] = "Percentile rank of rv_cc_48 within trailing 30 days (min 5d warmup)."
    bar_range = (h - l) / (c + EPS)
    out["range_prank_30d"] = bar_range.rolling(W30D, min_periods=288 * 5).rank(pct=True)
    man["range_prank_30d"] = "Percentile rank of relative bar range (high-low)/close within trailing 30 days."

    # ---------------- multi-lag returns ----------------
    for k in (1, 3, 6, 12, 24, 48, 96, 288):
        rk = logc.diff(k)
        out[f"ret_{k}"] = rk
        man[f"ret_{k}"] = f"Log return over past {k} bars ({k*5} min)."
        out[f"retn_{k}"] = rk / (rv[48] * np.sqrt(k) + EPS)
        man[f"retn_{k}"] = f"ret_{k} normalized by rv_cc_48 * sqrt({k}) (trailing-vol units)."

    # ---------------- trend ----------------
    for w in (12, 48, 288):
        net = (c - c.shift(w)).abs()
        path = c.diff().abs().rolling(w, min_periods=w).sum()
        out[f"er_{w}"] = net / (path + EPS)
        man[f"er_{w}"] = f"Kaufman efficiency ratio |net move|/sum|moves| over {w} bars (0=chop, 1=trend)."

    # ADX-14 (Wilder)
    up_m = h.diff()
    dn_m = -l.diff()
    plus_dm = pd.Series(np.where((up_m > dn_m) & (up_m > 0), up_m, 0.0), index=spot.index)
    minus_dm = pd.Series(np.where((dn_m > up_m) & (dn_m > 0), dn_m, 0.0), index=spot.index)
    tr = pd.concat([h - l, (h - c.shift()).abs(), (l - c.shift()).abs()], axis=1).max(axis=1)
    atr14 = tr.ewm(alpha=1 / 14, adjust=False, min_periods=14).mean()
    pdi = 100 * plus_dm.ewm(alpha=1 / 14, adjust=False, min_periods=14).mean() / (atr14 + EPS)
    mdi = 100 * minus_dm.ewm(alpha=1 / 14, adjust=False, min_periods=14).mean() / (atr14 + EPS)
    dx = 100 * (pdi - mdi).abs() / (pdi + mdi + EPS)
    out["adx_14"] = dx.ewm(alpha=1 / 14, adjust=False, min_periods=14).mean() / 100.0
    man["adx_14"] = "Wilder ADX(14) on 5m bars, scaled to [0,1]: trend strength regardless of direction."
    out["dmi_14"] = (pdi - mdi) / (pdi + mdi + EPS)
    man["dmi_14"] = "Signed directional index (+DI - -DI)/(+DI + -DI), Wilder 14: trend direction in [-1,1]."

    for w in (12, 48, 288):
        sma = c.rolling(w, min_periods=w).mean()
        sd = c.rolling(w, min_periods=w).std()
        out[f"sma_z_{w}"] = (c - sma) / (sd + EPS)
        man[f"sma_z_{w}"] = f"Distance of close from {w}-bar SMA in units of {w}-bar close std (trailing)."

    for w in (14, 48):
        d = c.diff()
        gain = d.clip(lower=0).ewm(alpha=1 / w, adjust=False, min_periods=w).mean()
        loss = (-d.clip(upper=0)).ewm(alpha=1 / w, adjust=False, min_periods=w).mean()
        out[f"rsi_{w}"] = gain / (gain + loss + EPS) - 0.5
        man[f"rsi_{w}"] = f"Wilder RSI({w}) rescaled to [-0.5, 0.5]."

    # ---------------- candle shape ----------------
    rng = (h - l)
    body = (c - o)
    out["body_range"] = (body / rng.replace(0, np.nan)).fillna(0.0)
    man["body_range"] = "Candle body / range (close-open)/(high-low), 0 when range is 0; in [-1,1]."
    out["wick_up_share"] = ((h - np.maximum(o, c)) / rng.replace(0, np.nan)).fillna(0.0)
    man["wick_up_share"] = "Upper wick as a share of bar range; 0 on zero-range bars."
    out["wick_dn_share"] = ((np.minimum(o, c) - l) / rng.replace(0, np.nan)).fillna(0.0)
    man["wick_dn_share"] = "Lower wick as a share of bar range; 0 on zero-range bars."
    out["body_z_48"] = zscore(body / (c + EPS), 48)
    man["body_z_48"] = "Signed relative candle body (close-open)/close, z-scored over trailing 48 bars."

    sgn = np.sign(r1).fillna(0.0)
    grp = (sgn != sgn.shift()).cumsum()
    runlen = sgn.groupby(grp).cumcount() + 1
    out["consec_runs"] = (runlen * sgn).clip(-12, 12) / 12.0
    man["consec_runs"] = "Signed count of consecutive same-sign 5m closes (capped at 12, scaled to [-1,1])."

    # ---------------- VWAP deviation ----------------
    for w in (12, 48, 288):
        vwap = qvol.rolling(w, min_periods=w).sum() / (vol.rolling(w, min_periods=w).sum() + EPS)
        out[f"vwap_dev_{w}"] = (c - vwap) / (atr14 + EPS)
        man[f"vwap_dev_{w}"] = f"(close - {w}-bar rolling VWAP) / ATR(14). VWAP = sum(quote_vol)/sum(volume)."

    # ---------------- volume / flow ----------------
    lvol = np.log1p(vol)
    for w in (12, 48, 288):
        out[f"vol_z_{w}"] = zscore(lvol, w)
        man[f"vol_z_{w}"] = f"z-score of log(1+volume) over trailing {w} bars."
    ltr = np.log1p(trades)
    out["trades_z_48"] = zscore(ltr, 48)
    man["trades_z_48"] = "z-score of log(1+trade count) over trailing 48 bars."
    out["trades_z_288"] = zscore(ltr, 288)
    man["trades_z_288"] = "z-score of log(1+trade count) over trailing 288 bars."

    ti = (2 * taker / vol.replace(0, np.nan) - 1).fillna(0.0).clip(-1, 1)
    out["ti_raw"] = ti
    man["ti_raw"] = "Signed taker imbalance 2*taker_base/volume - 1 (spot), in [-1,1]; 0 on empty bars."
    for span in (6, 12, 48):
        out[f"ti_ema{span}"] = ti.ewm(span=span, adjust=False, min_periods=span).mean()
        man[f"ti_ema{span}"] = f"EMA(span={span}) of spot taker imbalance."
    out["ti_z_48"] = zscore(ti, 48)
    man["ti_z_48"] = "z-score of spot taker imbalance over trailing 48 bars."
    ti_sgn = np.sign(ti)
    out["ti_persist_12"] = ti_sgn.rolling(12, min_periods=12).sum() / 12.0
    man["ti_persist_12"] = "Mean sign of taker imbalance over last 12 bars (persistence, in [-1,1])."

    # ---------------- order-flow decomposition ----------------
    ats = np.log1p(vol / trades.replace(0, np.nan)).ffill(limit=12)
    ats_z48 = zscore(ats, 48)
    ats_z288 = zscore(ats, 288)
    out["ats_z_48"] = ats_z48
    man["ats_z_48"] = "Avg trade size log(1+volume/trades) z-scored over trailing 48 bars."
    out["ats_z_288"] = ats_z288
    man["ats_z_288"] = "Avg trade size log(1+volume/trades) z-scored over trailing 288 bars."

    large_flow = pd.Series(np.where(ats_z48 > 1.0, ti, 0.0), index=spot.index)
    small_flow = pd.Series(np.where(ats_z48 < -1.0, ti, 0.0), index=spot.index)
    out["large_flow_ema12"] = large_flow.ewm(span=12, adjust=False, min_periods=12).mean()
    man["large_flow_ema12"] = "EMA(12) of taker imbalance gated to bars with avg-trade-size z>1 (large/institutional flow)."
    out["large_flow_ema48"] = large_flow.ewm(span=48, adjust=False, min_periods=48).mean()
    man["large_flow_ema48"] = "EMA(48) of large-trade-gated taker imbalance."
    out["small_flow_ema12"] = small_flow.ewm(span=12, adjust=False, min_periods=12).mean()
    man["small_flow_ema12"] = "EMA(12) of taker imbalance gated to bars with avg-trade-size z<-1 (retail/small flow)."
    out["lf_x_retn6"] = out["large_flow_ema12"] * out["retn_6"]
    man["lf_x_retn6"] = "Interaction: large-trade flow EMA12 * vol-normalized 6-bar return (does big flow confirm the move)."
    out["sf_x_retn6"] = out["small_flow_ema12"] * out["retn_6"]
    man["sf_x_retn6"] = "Interaction: small-trade flow EMA12 * vol-normalized 6-bar return (retail chasing proxy)."

    # ---------------- 1m sub-bar structure ----------------
    m1["bar"] = (m1["open_time"] // 300000) * 300000
    m1_ti = (2 * m1["taker_base"] / m1["volume"].replace(0, np.nan) - 1).fillna(0.0).clip(-1, 1)
    m1_logc = np.log(m1["close"])
    m1_r = m1_logc.diff()
    m1_sgn = np.sign(m1_r).fillna(0.0)
    same_bar = (m1["bar"] == m1["bar"].shift()).astype(float)
    agree = ((m1_sgn * m1_sgn.shift()) > 0).astype(float) * same_bar

    g = pd.DataFrame({
        "bar": m1["bar"], "ti": m1_ti, "r": m1_r, "absr": m1_r.abs(), "agree": agree,
    }).groupby("bar")
    agg = pd.DataFrame({
        "m1_ti_mean": g["ti"].mean(),
        "m1_ti_last": g["ti"].last(),
        "m1_absr_max": g["absr"].max(),
        "m1_agree": g["agree"].sum(),
        "m1_r_last": g["r"].last(),
    })
    agg = agg.reindex(grid)
    range5_log = np.log(h / l.replace(0, np.nan)).replace(0, np.nan)
    out["m1_ti_mean"] = agg["m1_ti_mean"].to_numpy()
    man["m1_ti_mean"] = "Mean of 1m taker imbalances inside the 5m bar."
    out["m1_ti_last"] = agg["m1_ti_last"].to_numpy()
    man["m1_ti_last"] = "Taker imbalance of the last 1m sub-bar (freshest flow)."
    out["m1_maxmove_share"] = (agg["m1_absr_max"].to_numpy() / range5_log).clip(0, 1)
    man["m1_maxmove_share"] = "Max |1m log move| inside the bar / 5m log range: 1 = move concentrated in one minute."
    out["m1_sign_agree"] = (agg["m1_agree"].to_numpy() / 4.0) * 2.0 - 1.0
    man["m1_sign_agree"] = "Within-bar consecutive 1m return sign-agreement count (0-4) scaled to [-1,1]: momentum vs chop."
    out["m1_last_retn"] = agg["m1_r_last"].to_numpy() / (rv[48] / np.sqrt(5) + EPS)
    man["m1_last_retn"] = "Last 1m log return normalized by 1m-scaled trailing vol (rv_cc_48/sqrt(5)): freshest momentum."
    # 1m source validated full-grid; any isolated miss -> neutral fill
    for col in ("m1_ti_mean", "m1_ti_last", "m1_sign_agree"):
        out[col] = out[col].fillna(0.0)

    # ---------------- perp tape ----------------
    pvol, ptaker = perp["volume"], perp["taker_base"]
    pti = (2 * ptaker / pvol.replace(0, np.nan) - 1).fillna(0.0).clip(-1, 1)
    pti = pti.set_axis(perp["open_time"]).reindex(grid).fillna(0.0).reset_index(drop=True)
    pvol_g = pvol.set_axis(perp["open_time"]).reindex(grid).reset_index(drop=True)
    out["perp_ti_ema12"] = pti.ewm(span=12, adjust=False, min_periods=12).mean()
    man["perp_ti_ema12"] = "EMA(12) of perp taker imbalance."
    out["perp_ti_z_48"] = zscore(pti, 48)
    man["perp_ti_z_48"] = "z-score of perp taker imbalance over trailing 48 bars."
    pshare = pvol_g / (pvol_g + vol + EPS)
    out["perp_share_z_288"] = zscore(pshare.ffill(limit=12), 288)
    man["perp_share_z_288"] = "Perp volume share perp/(perp+spot), z-scored over trailing 288 bars (ffill limit 12)."
    out["perp_spot_ti_gap"] = out["perp_ti_ema12"] - out["ti_ema12"]
    man["perp_spot_ti_gap"] = "Perp minus spot taker-imbalance EMA(12) gap: derivative-led vs spot-led flow."

    # ---------------- session (pure calendar) ----------------
    ts = pd.to_datetime(out["open_time"], unit="ms", utc=True)
    hour_f = ts.dt.hour + ts.dt.minute / 60.0
    out["sess_hour_sin"] = np.sin(2 * np.pi * hour_f / 24.0)
    man["sess_hour_sin"] = "sin of UTC hour-of-day (5m resolution)."
    out["sess_hour_cos"] = np.cos(2 * np.pi * hour_f / 24.0)
    man["sess_hour_cos"] = "cos of UTC hour-of-day."
    dow = ts.dt.dayofweek
    out["sess_dow_sin"] = np.sin(2 * np.pi * dow / 7.0)
    man["sess_dow_sin"] = "sin of day-of-week (Mon=0)."
    out["sess_dow_cos"] = np.cos(2 * np.pi * dow / 7.0)
    man["sess_dow_cos"] = "cos of day-of-week."
    out["sess_weekend"] = (dow >= 5).astype(float)
    man["sess_weekend"] = "Weekend flag (Sat/Sun UTC)."
    mins = ts.dt.hour * 60 + ts.dt.minute
    out["sess_us_cash"] = ((mins >= 13 * 60 + 30) & (mins < 20 * 60)).astype(float)
    man["sess_us_cash"] = "US cash-equity hours flag (13:30-20:00 UTC)."
    out["sess_asia"] = (ts.dt.hour < 8).astype(float)
    man["sess_asia"] = "Asia session flag (00:00-08:00 UTC)."

    feat_cols = [col for col in out.columns if col != "open_time"]
    out[feat_cols] = out[feat_cols].astype(np.float64)
    out = out.replace([np.inf, -np.inf], np.nan)
    return out, man


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--end", default="2026-06-01")
    args = ap.parse_args()

    out, man = build(args.end)
    pq = os.path.join(HERE, "price_flow.parquet")
    out.to_parquet(pq, index=False)
    with open(os.path.join(HERE, "price_flow_manifest.json"), "w") as f:
        json.dump(man, f, indent=2)

    # validation report
    feat_cols = [col for col in out.columns if col != "open_time"]
    n_inf = int(np.isinf(out[feat_cols].to_numpy()).sum())
    print(f"rows={len(out)} features={len(feat_cols)} inf_values={n_inf}")
    tail = out.iloc[5000:]
    nanpct = (tail[feat_cols].isna().mean() * 100).sort_values(ascending=False)
    print("NaN% after bar 5000 (worst 15):")
    print(nanpct.head(15).to_string(float_format=lambda x: f"{x:.3f}"))
    bad = nanpct[nanpct > 5.0]
    if len(bad):
        print("WARNING: columns >5% NaN after bar 5000:", list(bad.index))
    else:
        print("All columns <=5% NaN after bar 5000.")


if __name__ == "__main__":
    main()
