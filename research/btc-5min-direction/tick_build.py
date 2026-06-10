"""Resumable builder of SUB-BAR microstructure features from Binance aggTrades (tick) data.
For each recent day: download the daily aggTrades zip, parse ~1M trades, aggregate to 5-min
bars with features bar data CANNOT contain. Saves one CSV per day in /tmp/tickfeat; run
repeatedly to finish all days, then assemble tick_5m_features.csv.

aggTrades cols: 0 aggId,1 price,2 qty,3 firstId,4 lastId,5 ts,6 isBuyerMaker,7 isBestMatch
isBuyerMaker=True  -> buyer was maker -> SELL-aggressor.  False -> BUY-aggressor.
"""
import urllib.request, zipfile, io, os, time, glob
from datetime import date, timedelta
import numpy as np, pandas as pd

END = date(2026, 5, 28); NDAYS = 30
dates = [(END - timedelta(days=i)).isoformat() for i in range(NDAYS)][::-1]
os.makedirs("/tmp/tickfeat", exist_ok=True)
BASE = "https://data.binance.vision/data/spot/daily/aggTrades/BTCUSDT/BTCUSDT-aggTrades-"

def process_day(d):
    out = f"/tmp/tickfeat/{d}.csv"
    if os.path.exists(out):
        return "skip"
    try:
        req = urllib.request.Request(BASE + d + ".zip", headers={"User-Agent": "research"})
        with urllib.request.urlopen(req, timeout=25) as r:
            raw = r.read()
        z = zipfile.ZipFile(io.BytesIO(raw))
        df = pd.read_csv(z.open(z.namelist()[0]), header=None,
                         usecols=[1, 2, 5, 6], names=["price", "qty", "ts", "bm"])
    except Exception as e:
        return f"FAIL {d} {type(e).__name__}"
    ts = df["ts"].values
    if ts[0] > 1e15: ts = ts // 1000          # microseconds -> ms
    bar = (ts // 300000) * 300000
    off = ts - bar                            # ms into the bar (0..300000)
    price = df["price"].values; qty = df["qty"].values
    buy = (df["bm"].values == False)          # buy-aggressor
    sgn = np.where(buy, qty, -qty)            # signed volume
    notional = qty * price
    large = notional > 100000                 # >$100k "whale" prints
    endmask = off >= 200000                   # last third of the bar
    g = pd.DataFrame({"bar": bar, "qty": qty, "sgn": sgn, "buy": buy.astype(float),
                      "large_sgn": np.where(large, sgn, 0.0), "large_vol": np.where(large, qty, 0.0),
                      "end_sgn": np.where(endmask, sgn, 0.0), "end_vol": np.where(endmask, qty, 0.0),
                      "logp": np.log(price)})
    gb = g.groupby("bar")
    feat = pd.DataFrame({
        "open_time": gb.size().index.values,
        "tick_n": gb.size().values,
        "tick_vol": gb["qty"].sum().values,
        "tick_ofi": (gb["sgn"].sum() / (gb["qty"].sum() + 1e-9)).values,
        "tick_abs_ofi": (gb["sgn"].sum().abs() / (gb["qty"].sum() + 1e-9)).values,
        "tick_buy_frac": gb["buy"].mean().values,
        "tick_avg_size": (gb["qty"].sum() / gb.size()).values,
        "whale_ofi": (gb["large_sgn"].sum() / (gb["large_vol"].sum() + 1e-9)).values,
        "whale_vol_frac": (gb["large_vol"].sum() / (gb["qty"].sum() + 1e-9)).values,
        "end_ofi": (gb["end_sgn"].sum() / (gb["end_vol"].sum() + 1e-9)).values,
        "tick_rv": gb["logp"].std().values,
    })
    feat.to_csv(out, index=False)
    return f"ok {d} bars={len(feat)}"

t0 = time.time(); done = 0
for d in dates:
    if time.time() - t0 > 36: break
    r = process_day(d)
    if r != "skip": done += 1
    if r.startswith("FAIL"): print(r)
present = len(glob.glob("/tmp/tickfeat/*.csv"))
print(f"days_present={present}/{NDAYS} processed_now={done} elapsed={round(time.time()-t0,1)}")

if present >= NDAYS:
    allf = pd.concat([pd.read_csv(f) for f in glob.glob("/tmp/tickfeat/*.csv")]).drop_duplicates("open_time").sort_values("open_time")
    allf.to_csv("tick_5m_features.csv", index=False)
    print(f"ASSEMBLED tick_5m_features.csv rows={len(allf)} cols={list(allf.columns)}")
