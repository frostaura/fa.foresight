"""Download Binance USDT-perp data for BTC: funding rate (full history) + perp 5m klines.
Funding -> funding.csv ; perp klines -> perp_5m.csv (resumable parts in /tmp/perp_parts)."""
import urllib.request, json, time, os, glob
from concurrent.futures import ThreadPoolExecutor, as_completed
import pandas as pd

HOST = "https://fapi.binance.com"
def get(url):
    req = urllib.request.Request(url, headers={"User-Agent": "research"})
    with urllib.request.urlopen(req, timeout=20) as r: return json.load(r)

now = int(time.time() * 1000)
start = now - int(15 * 30 * 86400 * 1000)

# ---- 1. Funding rate (8h), paged ----
if not os.path.exists("funding.csv"):
    rows = []; cur = start
    while cur < now:
        d = get(f"{HOST}/fapi/v1/fundingRate?symbol=BTCUSDT&startTime={cur}&endTime={now}&limit=1000")
        if not d: break
        rows.extend(d); last = d[-1]["fundingTime"]
        if last <= cur: break
        cur = last + 1
        if len(d) < 1000: break
        time.sleep(0.1)
    f = pd.DataFrame(rows)[["fundingTime", "fundingRate", "markPrice"]].drop_duplicates("fundingTime")
    f["fundingRate"] = f["fundingRate"].astype(float); f["markPrice"] = f["markPrice"].astype(float)
    f.to_csv("funding.csv", index=False)
    print(f"funding.csv rows={len(f)} range={pd.to_datetime(f.fundingTime.iloc[0],unit='ms')}..{pd.to_datetime(f.fundingTime.iloc[-1],unit='ms')}")
else:
    print("funding.csv exists")

# ---- 2. Perp 5m klines (parallel, resumable) ----
ms5 = 5*60*1000; win = 1000*ms5
windows = list(range(start - start % ms5, now, win))
pdir = "/tmp/perp_parts"; os.makedirs(pdir, exist_ok=True)
def fetch(i_st):
    i, st = i_st; pf = f"{pdir}/p_{i:04d}.json"
    if os.path.exists(pf) and os.path.getsize(pf) > 10:
        try:
            d = json.load(open(pf))
            if isinstance(d, list) and len(d) > 0: return "skip"
        except: pass
    url = f"{HOST}/fapi/v1/klines?symbol=BTCUSDT&interval=5m&startTime={st}&limit=1000"
    for a in range(5):
        try:
            d = get(url)
            if isinstance(d, list) and len(d) > 0:
                json.dump(d, open(pf, "w")); return len(d)
        except Exception: time.sleep(1.2*(a+1))
    return 0
t0 = time.time()
with ThreadPoolExecutor(max_workers=8) as ex:
    futs = [ex.submit(fetch, (i, st)) for i, st in enumerate(windows)]
    for fu in as_completed(futs):
        if time.time()-t0 > 34: break
present = len(glob.glob(f"{pdir}/p_*.json"))
print(f"perp windows={len(windows)} parts={present} elapsed={round(time.time()-t0,1)}")
if present >= len(windows):
    rows = []
    for pf in glob.glob(f"{pdir}/p_*.json"):
        try:
            d = json.load(open(pf))
            if isinstance(d, list): rows.extend(d)
        except: pass
    cols = ["open_time","open","high","low","close","volume","close_time","quote_vol","trades","taker_base","taker_quote","ignore"]
    df = pd.DataFrame(rows, columns=cols)
    for c in ["open","high","low","close","volume","quote_vol","taker_base","taker_quote"]: df[c]=df[c].astype(float)
    df = df.drop_duplicates("open_time").sort_values("open_time").reset_index(drop=True).iloc[:-1]
    df = df[["open_time","open","high","low","close","volume","quote_vol","trades","taker_base","taker_quote"]]
    df.to_csv("perp_5m.csv", index=False)
    print(f"ASSEMBLED perp_5m.csv rows={len(df)}")
