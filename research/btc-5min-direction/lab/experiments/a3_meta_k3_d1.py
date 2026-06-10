"""a3_meta_k3 — meta-labeling (Lopez de Prado) wrapper on the K=3 baseline LGBM.

Design (pre-registered):
  * Primary = baseline LGBM (exp_baseline_k3 hyperparams, n_jobs=3).
  * Inside fit(X, y): chronological split of the fit data; primary trained on the
    early part, OOS probs on the later part(s) (purged 70 bars) label a meta target
    "was the primary's side correct"; a small meta-LGBM (150 trees, depth 4) learns
    P(correct | primary p, |p-0.5|, ~22 regime columns).
  * VARIANT is set by MODE below ("d1" single 75/25 split, "d2" cross-fitted
    50->75 + 75->100 expanding folds) — chosen on pre-window sweep before any
    harness run.
  * Primary is refit on ALL fit data at the end; meta stays from the split(s).
  * predict_proba: q = 0.5 + sign(p-0.5) * m * |p-0.5| (direction from primary,
    magnitude shrunk by meta P(correct); isotonic in the harness recalibrates).

K=3, PRIMARY_COV=0.025 (declared before first harness run).
"""
import numpy as np
import lightgbm as lgb

NAME = "a3_meta_k3_d1"
K = 3
PRIMARY_COV = 0.025
MODE = "d1"      # literal assigned design: single 75/25 split
PURGE = 70

# Exact dataframe column order of features_v1.pkl minus reserved cols — pinned so
# the wrapper can find regime columns by index inside the raw X the harness passes.
FEATURES = ['rv_cc_12', 'rv_cc_48', 'rv_cc_288', 'rv_gk_12', 'rv_gk_48', 'rv_gk_288', 'vov_48', 'rv48_prank_30d', 'range_prank_30d', 'ret_1', 'retn_1', 'ret_3', 'retn_3', 'ret_6', 'retn_6', 'ret_12', 'retn_12', 'ret_24', 'retn_24', 'ret_48', 'retn_48', 'ret_96', 'retn_96', 'ret_288', 'retn_288', 'er_12', 'er_48', 'er_288', 'adx_14', 'dmi_14', 'sma_z_12', 'sma_z_48', 'sma_z_288', 'rsi_14', 'rsi_48', 'body_range', 'wick_up_share', 'wick_dn_share', 'body_z_48', 'consec_runs', 'vwap_dev_12', 'vwap_dev_48', 'vwap_dev_288', 'vol_z_12', 'vol_z_48', 'vol_z_288', 'trades_z_48', 'trades_z_288', 'ti_raw', 'ti_ema6', 'ti_ema12', 'ti_ema48', 'ti_z_48', 'ti_persist_12', 'ats_z_48', 'ats_z_288', 'large_flow_ema12', 'large_flow_ema48', 'small_flow_ema12', 'lf_x_retn6', 'sf_x_retn6', 'm1_ti_mean', 'm1_ti_last', 'm1_maxmove_share', 'm1_sign_agree', 'm1_last_retn', 'perp_ti_ema12', 'perp_ti_z_48', 'perp_share_z_288', 'perp_spot_ti_gap', 'sess_hour_sin', 'sess_hour_cos', 'sess_dow_sin', 'sess_dow_cos', 'sess_weekend', 'sess_us_cash', 'sess_asia', 'doi_1b_z', 'doi_4b_z', 'doi_12b_z', 'doi_48b_z', 'doi_288b_z', 'oi_fuel', 'oi_fuel_z', 'oi_pctile_30d', 'deleverage_dir', 'oi_flush_flag', 'oi_spike_flat', 'cascade_signed', 'cascade_decel', 'tt_pos_ls_z', 'tt_pos_ls_chg4h_z', 'tt_acct_ls_z', 'tt_acct_ls_chg4h_z', 'global_ls_z', 'global_ls_chg4h_z', 'taker_ls_z', 'taker_ls_chg4h_z', 'taker_ls_z_ema', 'ls_divergence', 'funding_last_z', 'funding_pctile_90d', 'pred_funding_bps', 'pred_funding_z', 'mins_to_funding', 'funding_cycle_sin', 'funding_cycle_cos', 'prefund_extreme', 'crowding_score', 'prem_bps', 'prem_z96', 'prem_chg12_bps', 'basis_bps', 'basis_z288', 'basis_chg12_z', 'deribit_fund_spread_bps', 'deribit_fund_spread_z', 'xa_eth_ret1_vn', 'xa_eth_ret3_vn', 'xa_eth_ret12_vn', 'xa_sol_ret1_vn', 'xa_sol_ret3_vn', 'xa_sol_ret12_vn', 'xa_bnb_ret1_vn', 'xa_bnb_ret3_vn', 'xa_bnb_ret12_vn', 'xa_ethbtc_rs_1h_z', 'xa_ethbtc_rs_4h_z', 'xa_solbtc_rs_1h_z', 'xa_solbtc_rs_4h_z', 'xa_btc_eth_corr288', 'xa_btc_eth_corr288_chg', 'xa_mean_corr288', 'xa_mean_corr288_chg', 'xa_breadth_15m', 'xa_breadth_1h', 'xa_breadth_4h', 'xa_breadth_1h_beta', 'xa_breadth_div', 'xa_frac_alts_up_4h', 'xa_dispersion_1h', 'xa_dispersion_1h_z', 'xa_amp_ratio', 'xa_amp_ratio_z', 'xa_alt_taker_imb_ema', 'xa_alt_taker_imb_z', 'xa_alt_volsurge_z288', 'xa_altbtc_vol_ratio_z', 'xa_btc_volshare_z', 'xa_cb_prem_bps', 'xa_cb_prem_ema3', 'xa_cb_prem_z7d', 'xa_cb_prem_chg12', 'xa_cb_prem_us', 'xa_cb_volshare_z', 'xa_dvol_pct90d', 'xa_dvol_chg12h_z', 'xa_dvol_chg24h_z', 'xa_dvol_vel_z', 'xa_vrp', 'xa_vrp_z', 'xa_altidx_ret1_vn', 'xa_altidx_ret12_vn']

META_COLS = ["rv48_prank_30d", "range_prank_30d", "vov_48", "er_12", "er_48", "er_288",
             "adx_14", "xa_dvol_pct90d", "xa_dvol_chg12h_z", "xa_dvol_vel_z", "xa_vrp_z",
             "xa_breadth_1h", "xa_breadth_4h", "xa_mean_corr288", "xa_dispersion_1h_z",
             "funding_pctile_90d", "pred_funding_z", "oi_fuel_z", "oi_pctile_30d",
             "crowding_score", "sess_us_cash", "sess_asia"]
REG_IDX = np.array([FEATURES.index(c) for c in META_COLS])


def _primary():
    return lgb.LGBMClassifier(n_estimators=350, learning_rate=0.03, num_leaves=47,
                              max_depth=6, min_child_samples=150, subsample=0.8,
                              subsample_freq=1, colsample_bytree=0.6, reg_lambda=8.0,
                              random_state=42, n_jobs=3, verbose=-1)


def _meta():
    return lgb.LGBMClassifier(n_estimators=150, learning_rate=0.05, max_depth=4,
                              num_leaves=15, min_child_samples=100, subsample=0.8,
                              subsample_freq=1, colsample_bytree=0.8, reg_lambda=5.0,
                              random_state=42, n_jobs=3, verbose=-1)


def _meta_X(p, Xreg):
    return np.column_stack([p, np.abs(p - 0.5), Xreg])


class MetaLabelModel:
    def __init__(self, mode):
        self.mode = mode
        self.primary = None
        self.meta = None

    def fit(self, X, y, sample_weight=None):
        X = np.asarray(X, dtype=np.float64)
        y = np.asarray(y).astype(int)
        n = len(y)
        cut75 = int(n * 0.75)
        prim75 = _primary(); prim75.fit(X[:cut75], y[:cut75])
        b = slice(cut75 + PURGE, n)
        p_b = prim75.predict_proba(X[b])[:, 1]
        corr_b = ((p_b > 0.5).astype(int) == y[b]).astype(int)
        mXb = _meta_X(p_b, X[b][:, REG_IDX])
        if self.mode == "d1":
            mX, my = mXb, corr_b
        else:  # d2: add an earlier expanding fold 50 -> 75
            cut50 = int(n * 0.50)
            prim50 = _primary(); prim50.fit(X[:cut50], y[:cut50])
            f1 = slice(cut50 + PURGE, cut75)
            p_f1 = prim50.predict_proba(X[f1])[:, 1]
            corr_f1 = ((p_f1 > 0.5).astype(int) == y[f1]).astype(int)
            mX = np.vstack([_meta_X(p_f1, X[f1][:, REG_IDX]), mXb])
            my = np.concatenate([corr_f1, corr_b])
        self.meta = _meta(); self.meta.fit(mX, my)
        self.primary = _primary(); self.primary.fit(X, y)   # refit on ALL fit data
        return self

    def predict_proba(self, X):
        X = np.asarray(X, dtype=np.float64)
        p = self.primary.predict_proba(X)[:, 1]
        m = self.meta.predict_proba(_meta_X(p, X[:, REG_IDX]))[:, 1]
        q = 0.5 + np.sign(p - 0.5) * m * np.abs(p - 0.5)
        q = np.clip(q, 1e-6, 1 - 1e-6)
        return np.column_stack([1 - q, q])


def make_model():
    return MetaLabelModel(MODE)
