"""Parallel, resumable downloader for one Binance symbol -> {symbol}_5m.csv.
Usage: python download_asset.py SYMBOL [months]   e.g. python download_asset.py ETHUSDT 15
Call repeatedly to resume; assembles the CSV once all windows are present."""
import urllib.request, json, time, os, sys, glob
from concurrent.futures import ThreadPoolExecutor, as_completed
import pandas as pd

SYMBOL = sys.argv[1]
MONTHS = int(sys.argv[2]) if len(sys.argv) > 2 else 15
INTERVAL, LIMIT, HOST = "5m", 1000, "https://data-api.binance.vision"
ms5 = 5 * 60 * 1000; win = LIMIT * ms5; now = int(time.time() * 1000)
start = ((now - MONTHS * 30 * 86400 * 1000) // ms5) * ms5
windows = list(range(start, now, win))
pdir = f"parts_{SYMBOL}"; os.makedirs(pdir, exist_ok=True)

def fetch(i_st):
    i, st = i_st; pf = f"{pdir}/p_{i:04d}.json"
    if os.path.exists(pf) and os.path.getsize(pf) > 2: return "skip"
    url = f"{HOST}/api/v3/klines?symbol={SYMBOL}&interval={INTERVAL}&startTime={st}&limit={LIMIT}"
    for a in range(4):
        try:
            with urllib.request.urlopen(urllib.request.Request(url, headers={"User-Agent": "r"}), timeout=20) as r:
                json.dump(json.load(r), open(pf, "w")); return "ok"
        except Exception:
            time.sleep(1.2 * (a + 1))
    return "fail"

t0 = time.time()
with ThreadPoolExecutor(max_workers=10) as ex:
    futs = [ex.submit(fetch, (i, st)) for i, st in enumerate(windows)]
    for f in as_completed(futs):
        if time.time() - t0 > 38: break
present = len(glob.glob(f"{pdir}/p_*.json"))
print(f"{SYMBOL}: windows={len(windows)} parts={present} elapsed={round(time.time()-t0,1)}")

if present >= len(windows):
    rows = []
    for pf in glob.glob(f"{pdir}/p_*.json"):
        try: rows.extend(json.load(open(pf)))
        except: pass
    cols = ["open_time","open","high","low","close","volume","close_time","quote_vol","trades","taker_base","taker_quote","ignore"]
    df = pd.DataFrame(rows, columns=cols)
    for c in ["open","high","low","close","volume","quote_vol","taker_base","taker_quote"]:
        df[c] = df[c].astype(float)
    df["trades"] = df["trades"].astype(int)
    df = df.drop_duplicates("open_time").sort_values("open_time").reset_index(drop=True).iloc[:-1]
    df = df[["open_time","open","high","low","close","volume","quote_vol","trades","taker_base","taker_quote"]]
    df.to_csv(f"{SYMBOL}_5m.csv", index=False)
    print(f"ASSEMBLED {SYMBOL}_5m.csv rows={len(df)} range={pd.to_datetime(df.open_time.iloc[0],unit='ms')}..{pd.to_datetime(df.open_time.iloc[-1],unit='ms')}")
