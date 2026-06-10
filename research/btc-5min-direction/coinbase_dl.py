"""Resumable parallel downloader: Coinbase BTC-USD 5-min candles -> coinbase_btc_5m.csv.
Coinbase returns [time(s), low, high, open, close, volume], newest first, <=300/req.
Run repeatedly to resume; assembles once all windows present."""
import urllib.request, json, time, os, glob
from concurrent.futures import ThreadPoolExecutor, as_completed
import pandas as pd

GRAN = 300; PER = 300                       # 300s candles, 300 per request
HOST = "https://api.exchange.coinbase.com/products/BTC-USD/candles"
win_s = GRAN * PER                          # seconds per window
now = int(time.time())
start = now - int(15 * 30 * 86400)
start -= start % GRAN
windows = list(range(start, now, win_s))
pdir = "/tmp/cb_parts"; os.makedirs(pdir, exist_ok=True)

def fetch(i_st):
    i, st = i_st; pf = f"{pdir}/c_{i:04d}.json"
    if os.path.exists(pf) and os.path.getsize(pf) > 10:
        try:
            d = json.load(open(pf))
            if isinstance(d, list) and len(d) > 0: return "skip"
        except: pass
    en = min(st + win_s, now)
    url = f"{HOST}?granularity={GRAN}&start={st}&end={en}"
    for a in range(5):
        try:
            req = urllib.request.Request(url, headers={"User-Agent": "research"})
            with urllib.request.urlopen(req, timeout=20) as r:
                d = json.load(r)
            if isinstance(d, list):
                json.dump(d, open(pf, "w")); return len(d)
        except Exception:
            time.sleep(1.3 * (a + 1))
    return 0

t0 = time.time()
with ThreadPoolExecutor(max_workers=6) as ex:
    futs = [ex.submit(fetch, (i, st)) for i, st in enumerate(windows)]
    for f in as_completed(futs):
        if time.time() - t0 > 37: break
present = len(glob.glob(f"{pdir}/c_*.json"))
print(f"windows={len(windows)} parts={present} elapsed={round(time.time()-t0,1)}")

if present >= len(windows):
    rows = []
    for pf in glob.glob(f"{pdir}/c_*.json"):
        try:
            d = json.load(open(pf))
            if isinstance(d, list): rows.extend(d)
        except: pass
    df = pd.DataFrame(rows, columns=["t", "low", "high", "open", "close", "volume"])
    df["open_time"] = (df["t"].astype("int64")) * 1000
    df = df[["open_time", "open", "high", "low", "close", "volume"]].astype(
        {"open": float, "high": float, "low": float, "close": float, "volume": float})
    df = df.drop_duplicates("open_time").sort_values("open_time").reset_index(drop=True)
    df.to_csv("coinbase_btc_5m.csv", index=False)
    step = (df.open_time.diff().dropna() / 60000)
    print(f"ASSEMBLED coinbase_btc_5m.csv rows={len(df)} gaps={int((step!=5).sum())} "
          f"range={pd.to_datetime(df.open_time.iloc[0],unit='ms')}..{pd.to_datetime(df.open_time.iloc[-1],unit='ms')}")
