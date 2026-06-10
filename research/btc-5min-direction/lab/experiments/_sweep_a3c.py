"""Round 3: push the winning vol3_b05 mixture with internal bagging / capacity."""
import sys, os, time
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import numpy as np, pandas as pd, lightgbm
from _sweep_a3 import (RegimeMixture, run_config, BASE_PARAMS, BUCKET_PARAMS,
                       LAB, PRE_END, WIN_MS, K, EMB)


class RegimeMixtureBag(RegimeMixture):
    """Same as RegimeMixture but every model (global + buckets) is a 2-seed bag."""

    def __init__(self, specs, min_bucket=5000, blend=0.5, n_bag=2,
                 bucket_trees=250, global_trees=350):
        super().__init__(specs, min_bucket, blend)
        self.n_bag = n_bag
        self.bt = bucket_trees
        self.gt = global_trees

    def _bag(self, X, y, trees):
        ms = []
        for s in range(self.n_bag):
            p = dict(BASE_PARAMS, n_estimators=trees, random_state=42 + 1000 * s)
            m = lightgbm.LGBMClassifier(**p); m.fit(X, y); ms.append(m)
        return ms

    @staticmethod
    def _pred(ms, X):
        return np.mean([m.predict_proba(X)[:, 1] for m in ms], axis=0)

    def fit(self, X, y, sample_weight=None):
        y = np.asarray(y)
        self.edges_ = [np.quantile(X[:, ci], np.linspace(0, 1, nb + 1)[1:-1])
                       for ci, nb in self.specs]
        bid = self._bucket_ids(X)
        self.gms_ = self._bag(X, y, self.gt)
        self.bms_ = {}
        for b in np.unique(bid):
            m = bid == b
            if m.sum() >= self.min_bucket and len(np.unique(y[m])) == 2:
                self.bms_[int(b)] = self._bag(X[m], y[m], self.bt)
        return self

    def predict_proba(self, X):
        p = self._pred(self.gms_, X)
        bid = self._bucket_ids(X)
        for b, ms in self.bms_.items():
            m = bid == b
            if m.any():
                p[m] = self.blend * p[m] + (1 - self.blend) * self._pred(ms, X[m])
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
    vol = IDX["rv48_prank_30d"]
    configs = [
        ("mix_vol3_b05_bag2", lambda: RegimeMixtureBag([(vol, 3)], 5000, 0.5, 2)),
        ("mix_vol3_b05_t350", lambda: RegimeMixtureBag([(vol, 3)], 5000, 0.5, 1,
                                                       bucket_trees=350)),
        ("mix_vol2_b05_bag2", lambda: RegimeMixtureBag([(vol, 2)], 5000, 0.5, 2)),
    ]
    for name, mk in configs:
        t0 = time.time()
        run_config(name, mk, X, yK, fwdK, valid, ot, bounds)
        print(f"   ({time.time()-t0:.0f}s)", flush=True)

if __name__ == "__main__":
    main()
