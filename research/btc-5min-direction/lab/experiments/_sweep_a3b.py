"""Round 2: controls + blend tuning. Reuses _sweep_a3 machinery."""
import sys, os, time
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import numpy as np, pandas as pd, lightgbm
from _sweep_a3 import (RegimeMixture, run_config, BASE_PARAMS, BUCKET_PARAMS,
                       LAB, PRE_END, WIN_MS, K, EMB)

class BaggedGlobal:
    """Control: 2 global LGBMs (different seeds, 250 trees each) averaged.
    If this matches the regime mixture, the 'regime' part is doing nothing."""
    def __init__(self, n=2):
        self.n = n
    def fit(self, X, y, sample_weight=None):
        self.ms_ = []
        for s in range(self.n):
            p = dict(BASE_PARAMS, n_estimators=250, random_state=42 + s)
            m = lightgbm.LGBMClassifier(**p); m.fit(X, y); self.ms_.append(m)
        return self
    def predict_proba(self, X):
        p = np.mean([m.predict_proba(X)[:, 1] for m in self.ms_], axis=0)
        return np.column_stack([1 - p, p])

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
    vol = IDX["rv48_prank_30d"]; er = IDX["er_48"]
    configs = [
        ("ctrl_bagged_global2", lambda: BaggedGlobal(2)),
        ("mix_vol3_b03", lambda: RegimeMixture([(vol, 3)], 5000, 0.3)),
        ("mix_vol3_b07", lambda: RegimeMixture([(vol, 3)], 5000, 0.7)),
        ("mix_vol3xer2_b07", lambda: RegimeMixture([(vol, 3), (er, 2)], 5000, 0.7)),
        ("mix_vol2_b05", lambda: RegimeMixture([(vol, 2)], 5000, 0.5)),
    ]
    for name, mk in configs:
        t0 = time.time()
        run_config(name, mk, X, yK, fwdK, valid, ot, bounds)
        print(f"   ({time.time()-t0:.0f}s)", flush=True)

if __name__ == "__main__":
    main()
