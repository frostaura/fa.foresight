"""
Triple-barrier labeling (Lopez de Prado). For each bar t (decision at its close):
  entry = close[t];  sigma_H = per-bar vol * sqrt(H) (known at t, no look-ahead)
  upper = entry*(1+k*sigma_H), lower = entry*(1-k*sigma_H), vertical = t+H
  scan bars t+1..t+H: whichever horizontal barrier is touched FIRST sets the label.
  if neither: label by sign of (close[t+H]-close[t]).
The label uses FUTURE bars (it is the target). FEATURES use only the past.
Overlap (label windows of length H) is handled downstream by an embargo >= H.
"""
import numpy as np
import pandas as pd


def triple_barrier_labels(df, H=12, k=1.0, vol_span=100):
    c = df["close"].values.astype(float)
    high = df["high"].values.astype(float)
    low = df["low"].values.astype(float)
    n = len(c)
    ret1 = np.zeros(n); ret1[1:] = np.diff(c) / c[:-1]
    sigma = pd.Series(ret1).ewm(span=vol_span).std().bfill().values * np.sqrt(H)
    y = np.full(n, -1, dtype=int)     # -1 = undefined (tail)
    exit_ret = np.full(n, np.nan)
    touched = np.zeros(n, dtype=int)  # +1 upper, -1 lower, 0 vertical
    for t in range(n - H - 1):
        up = c[t] * (1 + k * sigma[t])
        dn = c[t] * (1 - k * sigma[t])
        lab = None
        for j in range(t + 1, t + H + 1):
            if high[j] >= up:
                lab, tch = 1, 1; ex = up / c[t] - 1; break
            if low[j] <= dn:
                lab, tch = 0, -1; ex = dn / c[t] - 1; break
        if lab is None:                 # vertical barrier
            ex = c[t + H] / c[t] - 1
            lab, tch = (1 if ex > 0 else 0), 0
        y[t] = lab; exit_ret[t] = ex; touched[t] = tch
    out = pd.DataFrame({"open_time": df["open_time"].values, "y_tb": y,
                        "exit_ret": exit_ret, "touched": touched})
    return out


if __name__ == "__main__":
    df = pd.read_csv("btc_5m.csv")
    for H, k in [(6, 1.0), (12, 1.0), (12, 1.5), (24, 1.0)]:
        lab = triple_barrier_labels(df, H=H, k=k)
        v = lab[lab.y_tb >= 0]
        vert = (v.touched == 0).mean()
        print(f"H={H:2d} k={k}: up_rate={v.y_tb.mean():.4f} vertical_frac={vert:.3f} "
              f"mean|exit|={np.abs(v.exit_ret).mean()*1e4:.1f}bps n={len(v)}")
