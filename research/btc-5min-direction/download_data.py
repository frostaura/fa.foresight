"""
Download ~15 months of BTC/USDT 5-minute OHLCV from Binance's public data feed
and save to btc_5m.csv / btc_5m.pkl. No API key required (public market data).

Usage:  python download_data.py [months]   (default 15)
"""
import sys, time, json, urllib.request
import pandas as pd

SYMBOL, INTERVAL, LIMIT = "BTCUSDT", "5m", 1000
HOST = "https://data-api.binance.vision"
MS5 = 5 * 60 * 1000
MONTHS = int(sys.argv[1]) if len(sys.argv) > 1 else 15


def fetch(start_ms):
    url = f"{HOST}/api/v3/klines?symbol={SYMBOL}&interval={INTERVAL}&startTime={start_ms}&limit={LIMIT}"
    req = urllib.request.Request(url, headers={"User-Agent": "research"})
    for attempt in range(5):
        try:
            with urllib.request.urlopen(req, timeout=20) as r:
                return json.load(r)
        except Exception as e:
            time.sleep(1.5 * (attempt + 1))
    raise RuntimeError(f"failed to fetch {start_ms}")


def main():
    now = int(time.time() * 1000)
    cur = ((now - MONTHS * 30 * 24 * 60 * 60 * 1000) // MS5) * MS5
    rows, calls = [], 0
    while cur < now:
        batch = fetch(cur)
        if not batch:
            break
        rows.extend(batch)
        calls += 1
        cur = batch[-1][0] + MS5
        if calls % 20 == 0:
            print(f"  {calls} requests, {len(rows)} bars...")
        time.sleep(0.1)

    cols = ["open_time", "open", "high", "low", "close", "volume", "close_time",
            "quote_vol", "trades", "taker_base", "taker_quote", "ignore"]
    df = pd.DataFrame(rows, columns=cols)
    for c in ["open", "high", "low", "close", "volume", "quote_vol", "taker_base", "taker_quote"]:
        df[c] = df[c].astype(float)
    df["trades"] = df["trades"].astype(int)
    df = (df.drop_duplicates("open_time").sort_values("open_time")
            .reset_index(drop=True).iloc[:-1])  # drop possibly-forming last bar
    df = df[["open_time", "open", "high", "low", "close", "volume", "quote_vol",
             "trades", "taker_base", "taker_quote"]]
    df.to_csv("btc_5m.csv", index=False)
    df.to_pickle("btc_5m.pkl")
    print(f"saved {len(df)} bars -> btc_5m.csv + btc_5m.pkl")


if __name__ == "__main__":
    main()
