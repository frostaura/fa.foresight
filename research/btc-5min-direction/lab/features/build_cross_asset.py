#!/usr/bin/env python3
"""
build_cross_asset.py -- FAMILY: cross_asset
The rest of the market as context: 17 alt/major spot files + Coinbase BTC + Deribit DVOL.

Deterministic. Usage:
    python3 build_cross_asset.py --end 2026-06-01

Output:
    <lab>/features/cross_asset.parquet   (open_time int64 ms + float64 features)
    <lab>/features/cross_asset_manifest.json

LEAKAGE NOTES
- All 5m series (Binance spot alts, Coinbase): bar at open_time=t closes at t+5m,
  same moment the BTC bar t closes -> same-open_time join is legal.
- Deribit DVOL 1h: bar starting T is complete at T+1h -> availability timestamp is
  shifted +1h, then as-of joined (avail <= t).
- Deribit DVOL 5m: same-open_time join legal (bar t closes at t+5m). Coalesce:
  5m value where present (starts 2025-12-12 08:05 UTC), else shifted-1h value.
  Splice documented in manifest; trailing z-windows absorb the granularity change.
- All rolling/EWM constructions are trailing-only. No shift(-n), no centered windows.

MID-SERIES NaN POLICY
- Alt closes: ffill limit 3 bars (15m) -- alts grid is validated/complete, defensive only.
- Coinbase close: ffill limit 3 bars; premium NaN beyond (omitted candles), then the
  premium itself is ffilled limit 6 (30m) to bridge tiny exchange gaps; NaN beyond.
- DVOL 1h: as-of join naturally carries last value forward (series is gap-free).
"""
import argparse
import json
import os

import numpy as np
import pandas as pd

LAB = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DATA = os.path.join(LAB, "data")
OUT_DIR = os.path.join(LAB, "features")

ALTS = ["ETHUSDT", "SOLUSDT", "BNBUSDT", "XRPUSDT", "DOGEUSDT", "ADAUSDT",
        "AVAXUSDT", "LINKUSDT", "DOTUSDT", "LTCUSDT", "BCHUSDT", "TRXUSDT",
        "ATOMUSDT", "UNIUSDT", "NEARUSDT", "APTUSDT", "FILUSDT"]

BAR_MS = 300_000
ANN_BARS = 365 * 288  # 5m bars per year

manifest = {}


def zscore(s, w, mp=None):
    """Trailing rolling z-score."""
    mp = mp or w // 2
    m = s.rolling(w, min_periods=mp).mean()
    sd = s.rolling(w, min_periods=mp).std()
    return (s - m) / sd.replace(0.0, np.nan)


def load_spot(sym, grid):
    df = pd.read_csv(os.path.join(DATA, f"spot_{sym}_5m.csv"),
                     usecols=["open_time", "close", "quote_vol", "taker_quote"])
    df = df.set_index("open_time").reindex(grid)
    df["close"] = df["close"].ffill(limit=3)
    return df


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--end", default="2026-06-01")
    args = ap.parse_args()
    end_ms = int(pd.Timestamp(args.end, tz="UTC").value // 10**6)

    # ------------------------------------------------------------------ grid
    btc = pd.read_csv(os.path.join(DATA, "spot_BTCUSDT_5m.csv"),
                      usecols=["open_time", "close", "quote_vol"])
    btc = btc[btc["open_time"] < end_ms].set_index("open_time").sort_index()
    grid = btc.index
    out = pd.DataFrame(index=grid)

    btc_ret1 = np.log(btc["close"]).diff()
    btc_vol1 = btc_ret1.ewm(span=288, min_periods=144).std()

    # ------------------------------------------------------------- alt loads
    closes, qvols, takerq = {}, {}, {}
    for s in ALTS:
        d = load_spot(s, grid)
        closes[s], qvols[s], takerq[s] = d["close"], d["quote_vol"], d["taker_quote"]
    C = pd.DataFrame(closes)          # alt closes
    QV = pd.DataFrame(qvols)          # alt quote vol
    TQ = pd.DataFrame(takerq)         # alt taker buy quote vol

    R1 = np.log(C).diff()             # 1-bar log returns per alt
    V1 = R1.ewm(span=288, min_periods=144).std()  # per-alt 1-bar trailing vol

    def hret(df, h):
        return np.log(df).diff(h)

    # ---------------------------------------------- 1) lead returns (vol-norm)
    for s, tag in [("ETHUSDT", "eth"), ("SOLUSDT", "sol"), ("BNBUSDT", "bnb")]:
        for h in (1, 3, 12):
            col = f"xa_{tag}_ret{h}_vn"
            out[col] = (hret(C[s], h) / (V1[s] * np.sqrt(h))).clip(-8, 8)
            manifest[col] = f"{tag.upper()} {h}-bar log return / its trailing EWM(288) 1-bar vol*sqrt({h}), clipped +-8"

    # ------------------------------------- 2) relative strength + correlation
    for s, tag in [("ETHUSDT", "ethbtc"), ("SOLUSDT", "solbtc")]:
        for h, hn in [(12, "1h"), (48, "4h")]:
            rs = hret(C[s], h) - np.log(btc["close"]).diff(h)
            col = f"xa_{tag}_rs_{hn}_z"
            out[col] = zscore(rs, 2016)
            manifest[col] = f"{tag[:3].upper()}-BTC {hn} relative log-return, z-scored over trailing 7d (2016 bars)"

    corr_eth = btc_ret1.rolling(288, min_periods=144).corr(R1["ETHUSDT"])
    out["xa_btc_eth_corr288"] = corr_eth
    manifest["xa_btc_eth_corr288"] = "Rolling 288-bar (24h) correlation of BTC and ETH 5m returns"
    out["xa_btc_eth_corr288_chg"] = corr_eth.diff(144)
    manifest["xa_btc_eth_corr288_chg"] = "12h change in BTC-ETH rolling 288-bar correlation"

    corr_all = pd.DataFrame({s: btc_ret1.rolling(288, min_periods=144).corr(R1[s]) for s in ALTS})
    mean_corr = corr_all.mean(axis=1)
    out["xa_mean_corr288"] = mean_corr
    manifest["xa_mean_corr288"] = "Mean rolling 288-bar correlation of BTC vs each of the 17 alts (coupling regime)"
    out["xa_mean_corr288_chg"] = mean_corr.diff(144)
    manifest["xa_mean_corr288_chg"] = "12h change in mean alt-BTC 288-bar correlation"

    # --------------------------------------------------------- 3) BREADTH (H8)
    btc_sign = {h: np.sign(np.log(btc["close"]).diff(h)) for h in (3, 12, 48)}
    for h, hn in [(3, "15m"), (12, "1h"), (48, "4h")]:
        Rh = np.log(C).diff(h)
        match = (np.sign(Rh).eq(btc_sign[h], axis=0)).where(Rh.notna())
        col = f"xa_breadth_{hn}"
        out[col] = match.mean(axis=1)
        manifest[col] = f"Fraction of 17 alts whose {hn} return sign matches BTC's {hn} return sign"

    # beta-adjusted breadth: vol-normalized alt 1h returns, signed by BTC direction
    Rh12 = np.log(C).diff(12)
    VN12 = (Rh12 / (V1 * np.sqrt(12))).clip(-3, 3)
    out["xa_breadth_1h_beta"] = VN12.mul(btc_sign[12], axis=0).mean(axis=1) / 3.0
    manifest["xa_breadth_1h_beta"] = "Beta-adjusted breadth: mean over alts of clipped vol-normalized 1h return * sign(BTC 1h return), scaled to ~[-1,1]"

    out["xa_breadth_div"] = out["xa_breadth_1h"] - out["xa_breadth_1h"].rolling(288, min_periods=144).mean()
    manifest["xa_breadth_div"] = "Breadth divergence: breadth_1h minus its trailing 24h (288-bar) mean"

    out["xa_frac_alts_up_4h"] = (np.sign(np.log(C).diff(48)) > 0).where(np.log(C).diff(48).notna()).mean(axis=1)
    manifest["xa_frac_alts_up_4h"] = "Unconditional breadth: fraction of 17 alts with positive 4h return"

    # ------------------------------------------------------------ 4) dispersion
    disp = VN12.std(axis=1)
    out["xa_dispersion_1h"] = disp
    manifest["xa_dispersion_1h"] = "Cross-sectional std of vol-normalized (clipped) 1h alt returns"
    out["xa_dispersion_1h_z"] = zscore(disp, 2016)
    manifest["xa_dispersion_1h_z"] = "Dispersion of vol-normalized 1h alt returns, z-scored over trailing 7d"

    btc_r12 = np.log(btc["close"]).diff(12)
    amp = Rh12.abs().mean(axis=1) / (btc_r12.abs() + 0.25 * btc_vol1 * np.sqrt(12))
    amp = np.log1p(amp).ewm(span=12, min_periods=6).mean()
    out["xa_amp_ratio"] = amp
    manifest["xa_amp_ratio"] = "log1p(mean |alt 1h ret| / (|BTC 1h ret| + 0.25*BTC 1h vol floor)), EMA(12) -- alt amplification of BTC moves"
    out["xa_amp_ratio_z"] = zscore(amp, 2016)
    manifest["xa_amp_ratio_z"] = "Amplification ratio z-scored over trailing 7d"

    # ----------------------------------------- 5) alt taker imbalance + volume
    imb = (2.0 * TQ / QV.replace(0.0, np.nan) - 1.0).clip(-1, 1)
    w = QV.rolling(288, min_periods=72).sum()
    w = w.div(w.sum(axis=1), axis=0)                      # cap-proxy weights (trailing 24h quote-vol share)
    wimb = (imb * w).sum(axis=1) / (w.where(imb.notna()).sum(axis=1).replace(0.0, np.nan))
    imb_ema = wimb.ewm(span=12, min_periods=6).mean()
    out["xa_alt_taker_imb_ema"] = imb_ema
    manifest["xa_alt_taker_imb_ema"] = "Quote-vol-share-weighted mean signed taker imbalance (2*taker_quote/quote_vol-1) across 17 alts, EMA(12)"
    out["xa_alt_taker_imb_z"] = zscore(imb_ema, 2016)
    manifest["xa_alt_taker_imb_z"] = "Weighted alt taker imbalance EMA, z-scored over trailing 7d"

    tot_alt_qv = QV.sum(axis=1)
    lv = np.log1p(tot_alt_qv)
    out["xa_alt_volsurge_z288"] = zscore(lv, 288, mp=144)
    manifest["xa_alt_volsurge_z288"] = "log total alt quote_vol, z-scored over trailing 24h (288 bars)"

    ratio = np.log1p(tot_alt_qv) - np.log1p(btc["quote_vol"])
    out["xa_altbtc_vol_ratio_z"] = zscore(ratio, 2016)
    manifest["xa_altbtc_vol_ratio_z"] = "log(total alt quote_vol / BTC quote_vol), z-scored over trailing 7d"

    btc_share = btc["quote_vol"] / (btc["quote_vol"] + tot_alt_qv)
    out["xa_btc_volshare_z"] = zscore(btc_share, 2016)
    manifest["xa_btc_volshare_z"] = "BTC share of total (BTC+alts) Binance quote volume, z-scored over trailing 7d (dominance-of-flow proxy)"

    # ------------------------------------------------- 6) COINBASE PREMIUM (H7)
    cb = pd.read_csv(os.path.join(DATA, "coinbase_btc_5m.csv"),
                     usecols=["open_time", "close", "volume"])
    cb = cb.set_index("open_time").reindex(grid)
    cb_close = cb["close"].ffill(limit=3)                 # omitted-candle handling: max 15m carry
    prem = (cb_close - btc["close"]) / btc["close"] * 1e4
    prem = prem.ffill(limit=6)                            # bridge residual micro-gaps (30m max), NaN beyond
    out["xa_cb_prem_bps"] = prem
    manifest["xa_cb_prem_bps"] = "Coinbase-Binance BTC premium in bps ((cb_close-bn_close)/bn_close*1e4); cb close ffilled max 3 bars"
    prem_ema = prem.ewm(span=3, min_periods=2).mean()
    out["xa_cb_prem_ema3"] = prem_ema
    manifest["xa_cb_prem_ema3"] = "Coinbase premium (bps), EMA(3)"
    out["xa_cb_prem_z7d"] = zscore(prem_ema, 2016)
    manifest["xa_cb_prem_z7d"] = "Coinbase premium EMA(3), z-scored over trailing 7d (2016 bars)"
    out["xa_cb_prem_chg12"] = prem_ema.diff(12)
    manifest["xa_cb_prem_chg12"] = "12-bar (1h) change in Coinbase premium EMA(3), bps"

    mins = ((grid // 60000) % 1440).astype(np.int64)
    us_sess = pd.Series(((mins >= 870) & (mins < 1260)).astype(np.float64), index=grid)  # 14:30-21:00 UTC
    out["xa_cb_prem_us"] = prem_ema * us_sess
    manifest["xa_cb_prem_us"] = "Coinbase premium EMA(3) * US-cash-session indicator (14:30-21:00 UTC), else 0"

    cb_usd = (cb["volume"].fillna(0.0) * cb_close)
    share = cb_usd / (cb_usd + btc["quote_vol"]).replace(0.0, np.nan)
    out["xa_cb_volshare_z"] = zscore(share, 2016)
    manifest["xa_cb_volshare_z"] = "Coinbase USD volume share of (Coinbase+Binance) BTC volume, z-scored over trailing 7d"

    # ------------------------------------------------------------- 7) DVOL (H?)
    # base: 1h series, availability = open_time + 1h, as-of join <= t
    d1 = pd.read_csv(os.path.join(DATA, "deribit_dvol_1h.csv"),
                     usecols=["open_time", "dvol_close"]).sort_values("open_time")
    d1["avail"] = d1["open_time"] + 3_600_000
    g = pd.DataFrame({"t": grid.to_numpy()})
    m = pd.merge_asof(g, d1[["avail", "dvol_close"]], left_on="t", right_on="avail",
                      direction="backward")
    dvol_1h = pd.Series(m["dvol_close"].to_numpy(), index=grid)

    # refine: 5m series (same-open_time join legal), coalesce where present.
    # Splice point = first 5m bar (2025-12-12 08:05 UTC); 1h-based value lags <=1h,
    # DVOL is slow-moving so the level splice is near-seamless; trailing z windows
    # absorb the granularity change within their 7d horizon.
    d5 = pd.read_csv(os.path.join(DATA, "deribit_dvol_5m.csv"),
                     usecols=["open_time", "dvol_close"]).sort_values("open_time")
    dvol_5m = d5.set_index("open_time")["dvol_close"].reindex(grid)
    dvol = dvol_5m.combine_first(dvol_1h)

    out["xa_dvol_pct90d"] = dvol.rolling(90 * 288, min_periods=30 * 288).rank(pct=True)
    manifest["xa_dvol_pct90d"] = "DVOL level percentile rank over trailing 90d (min 30d); spliced 1h(+1h shift)/5m series"
    out["xa_dvol_chg12h_z"] = zscore(dvol.diff(144), 2016)
    manifest["xa_dvol_chg12h_z"] = "12h DVOL change, z-scored over trailing 7d"
    out["xa_dvol_chg24h_z"] = zscore(dvol.diff(288), 2016)
    manifest["xa_dvol_chg24h_z"] = "24h DVOL change, z-scored over trailing 7d"
    out["xa_dvol_vel_z"] = zscore(dvol.diff(12), 2016)
    manifest["xa_dvol_vel_z"] = "DVOL velocity: 1h (12-bar) change z-scored over trailing 7d (OOD/crash veto input)"

    rv12h = np.sqrt(btc_ret1.pow(2).rolling(144, min_periods=72).mean() * ANN_BARS) * 100.0
    vrp = rv12h - dvol
    out["xa_vrp"] = vrp
    manifest["xa_vrp"] = "VRP proxy: trailing 12h annualized BTC realized vol (pct points) minus DVOL"
    out["xa_vrp_z"] = zscore(vrp, 2016)
    manifest["xa_vrp_z"] = "VRP proxy z-scored over trailing 7d"

    # ------------------------------------------------------- 8) alt index leads
    alt_idx_r1 = (R1 * w).sum(axis=1) / w.where(R1.notna()).sum(axis=1).replace(0.0, np.nan)
    idx_vol1 = alt_idx_r1.ewm(span=288, min_periods=144).std()
    out["xa_altidx_ret1_vn"] = (alt_idx_r1 / idx_vol1).clip(-8, 8)
    manifest["xa_altidx_ret1_vn"] = "Quote-vol-weighted alt index 1-bar return / its trailing EWM(288) vol, clipped +-8"
    out["xa_altidx_ret12_vn"] = (alt_idx_r1.rolling(12, min_periods=12).sum()
                                 / (idx_vol1 * np.sqrt(12))).clip(-8, 8)
    manifest["xa_altidx_ret12_vn"] = "Quote-vol-weighted alt index 12-bar (1h) return / trailing vol*sqrt(12), clipped +-8"

    # ----------------------------------------------------------------- output
    out = out.astype(np.float64).replace([np.inf, -np.inf], np.nan)
    res = out.reset_index().rename(columns={"index": "open_time", "open_time": "open_time"})
    res["open_time"] = res["open_time"].astype(np.int64)
    os.makedirs(OUT_DIR, exist_ok=True)
    pq = os.path.join(OUT_DIR, "cross_asset.parquet")
    res.to_parquet(pq, index=False)
    with open(os.path.join(OUT_DIR, "cross_asset_manifest.json"), "w") as f:
        json.dump(manifest, f, indent=2)

    # ------------------------------------------------------------- validation
    n_feat = res.shape[1] - 1
    print(f"rows={len(res)} (grid={len(grid)}) features={n_feat}")
    assert len(res) == len(grid)
    inf_ct = np.isinf(res.drop(columns=['open_time']).to_numpy()).sum()
    print("inf count:", inf_ct)
    tail = res.iloc[5000:].drop(columns=["open_time"])
    nan_pct = (tail.isna().mean() * 100).sort_values(ascending=False)
    print("NaN%% after bar 5000 (top 12):")
    print(nan_pct.head(12).to_string(float_format=lambda x: f"{x:.2f}"))
    full = res.drop(columns=["open_time"]).isna().mean() * 100
    print("overall NaN%% max:", f"{full.max():.2f}", "({})".format(full.idxmax()))


if __name__ == "__main__":
    main()
