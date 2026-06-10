"""Pre-window sweep: replicate harness fold logic on data BEFORE 2025-08-01.
Pseudo-windows: 6 x ~25d ending 2025-08-01. One LGBM fit per window, many gates."""
import warnings; warnings.filterwarnings("ignore")
import numpy as np, pandas as pd, lightgbm as lgb
from sklearn.isotonic import IsotonicRegression

K=3; EMB=K+60; CAL=0.10
CUTOFF=1754006400000
DAY=86400000; WLEN=int(25.3*DAY)
df=pd.read_pickle('features_v1.pkl')
df=df[df['open_time']<CUTOFF].reset_index(drop=True)
c=df['close'].values.astype(float); ot=df['open_time'].values.astype(np.int64)
fwd=np.full(len(c),np.nan); fwd[:-K]=c[K:]/c[:-K]-1
y=(fwd>0).astype(float)
drop={'open_time','close'}
cols=[x for x in df.columns if x not in drop]
X=df[cols].values.astype(np.float64)
valid=df[cols].notna().all(axis=1).values & ~np.isnan(fwd)

# event flags
E={}
E['cascade']=((df['cascade_signed'].fillna(0)!=0)|(df['cascade_decel'].fillna(0)==1)).values
E['oi_flush']=(df['oi_flush_flag'].fillna(0)==1).values
E['oi_spike_flat']=(df['oi_spike_flat'].fillna(0)==1).values
E['prem_z2']=(df['prem_z96'].abs()>2).values
E['basis_z2']=(df['basis_z288'].abs()>2).values
E['prefund']=(df['prefund_extreme'].fillna(0)!=0).values
E['postfund']=((df['mins_to_funding']>=420)&(df['funding_last_z'].abs()>1.5)).values
E['cb_us']=((df['xa_cb_prem_z7d'].abs()>2)&(df['sess_us_cash']==1)).values
U1=E['cascade']|E['oi_flush']|E['prem_z2']|E['prefund']|E['cb_us']
U2=U1|E['postfund']|E['oi_spike_flat']
U3=U2|E['basis_z2']
GATES={'ungated':np.ones(len(df),bool),'U1':U1,'U2':U2,'U3':U3}
for k_,v_ in E.items(): GATES['only_'+k_]=v_

bounds=[CUTOFF-(6-i)*WLEN for i in range(7)]
COVS=[0.10,0.05,0.025]
res={g:{cv:[] for cv in COVS} for g in GATES}   # list of (n,hit) per window
percls={k_:[] for k_ in E}  # trades within U2-gated selection per class @cov .05
for w in range(6):
    ws,we=bounds[w],bounds[w+1]
    te=np.where(valid&(ot>=ws)&(ot<we))[0]
    tr=np.where(valid&(ot<ws-EMB*300000))[0]
    if len(te)==0 or len(tr)<5000: print('skip',w); continue
    cut=int(len(tr)*0.9)
    fit_idx,cal_idx=tr[:cut],tr[cut:][EMB:]
    m=lgb.LGBMClassifier(n_estimators=350,learning_rate=0.03,num_leaves=47,max_depth=6,
        min_child_samples=150,subsample=0.8,subsample_freq=1,colsample_bytree=0.6,
        reg_lambda=8.0,random_state=42,n_jobs=3,verbose=-1)
    m.fit(X[fit_idx],y[fit_idx].astype(int))
    pc=m.predict_proba(X[cal_idx])[:,1]; iso=IsotonicRegression(out_of_bounds='clip')
    iso.fit(pc,y[cal_idx].astype(int)); p_cal=iso.transform(pc)
    p_te=iso.transform(m.predict_proba(X[te])[:,1])
    yte=y[te].astype(int)
    base_cal=np.abs(p_cal-0.5); base_te=np.abs(p_te-0.5)
    for g,act in GATES.items():
        s_cal=np.where(act[cal_idx],base_cal,-1.0); s_te=np.where(act[te],base_te,-1.0)
        for cv in COVS:
            thr=np.quantile(s_cal,1-cv); sel=s_te>=thr
            n=int(sel.sum())
            hit=float(((p_te[sel]>0.5).astype(int)==yte[sel]).mean()) if n else np.nan
            res[g][cv].append((n,hit))
            if g=='U2' and cv==0.05 and n:
                cor=((p_te[sel]>0.5).astype(int)==yte[sel]).astype(int)
                seli=te[sel]
                for k_ in E:
                    mm=E[k_][seli]
                    if mm.sum(): percls[k_].append((int(mm.sum()),float(cor[mm].mean())))
    print('win',w,'done',pd.to_datetime(ws,unit='ms').date())

print('\n=== pooled (trade-weighted) over 6 pre-windows ===')
for g in GATES:
    line=f'{g:16s}'
    for cv in COVS:
        rows=res[g][cv]; N=sum(n for n,_ in rows)
        H=sum(n*h for n,h in rows if n)/N if N else np.nan
        minw=min((n for n,_ in rows),default=0)
        nw50=sum(1 for n,h in rows if n>=30 and h>0.5)
        line+=f'  cov{cv}: n={N:5d} hit={H:.4f} minN={minw:4d} w>50:{nw50}/6'
    print(line)
print('\n=== per-class hit within U2-gated @cov0.05 ===')
for k_,rows in percls.items():
    N=sum(n for n,_ in rows)
    if N: print(f'{k_:14s} n={N:5d} hit={sum(n*h for n,h in rows)/N:.4f}')
