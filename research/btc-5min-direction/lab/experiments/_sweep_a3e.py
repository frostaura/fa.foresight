import sys, os, time
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import numpy as np, pandas as pd
from _sweep_a3 import RegimeMixture, run_config, LAB, PRE_END, WIN_MS, K

def main():
    N_WIN = 5
    df = pd.read_pickle(os.path.join(LAB, "features_v1.pkl"))
    df = df[df["open_time"] < PRE_END].reset_index(drop=True)
    cols = [c for c in df.columns if c not in ("open_time", "close")]
    IDX = {c: i for i, c in enumerate(cols)}
    c = df["close"].values.astype(float)
    ot = df["open_time"].values.astype(np.int64)
    fwdK = np.full(len(c), np.nan); fwdK[:-K] = c[K:] / c[:-K] - 1
    yK = (fwdK > 0).astype(float)
    valid = df[cols].notna().all(axis=1).values & ~np.isnan(fwdK)
    X = df[cols].values.astype(np.float64)
    bounds = [PRE_END - (N_WIN - i) * WIN_MS for i in range(N_WIN + 1)]
    vol = IDX["rv48_prank_30d"]
    for name, mk in [
        ("mix_vol6_b05", lambda: RegimeMixture([(vol, 6)], 5000, 0.5)),
        ("mix_vol8_b05", lambda: RegimeMixture([(vol, 8)], 5000, 0.5)),
    ]:
        t0 = time.time(); run_config(name, mk, X, yK, fwdK, valid, ot, bounds)
        print(f"   ({time.time()-t0:.0f}s)", flush=True)

if __name__ == "__main__":
    main()
