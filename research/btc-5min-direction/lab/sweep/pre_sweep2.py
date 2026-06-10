"""Sweep 2: forced-flow unions, widened funding windows, freq check + pooled hit."""
import warnings; warnings.filterwarnings("ignore")
import numpy as np, pandas as pd, lightgbm as lgb
from sklearn.isotonic import IsotonicRegression

K=3; EMB=K+60
CUTOFF=1754006400000
DAY=86400000; WLEN=int(25.3*DAY)
df=pd.read_pickle('features_v1.pkl')
dfa=df  # full for freq check
df=df[df['open_time']<CUTOFF].reset_index(drop=True)

def gates(d):
    G={}
    cascade=((d['cascade_signed'].fillna(0)!=0)|(d['cascade_decel'].fillna(0)==1)).values
    oifl=(d['oi_flush_flag'].fillna(0)==1).values
    pf15=(d['prefund_extreme'].fillna(0)!=0).values
    pof15=((d['mins_to_funding']>=420)&(d['funding_last_z'].abs()>1.5)).values
    pf10=((d['mins_to_funding']<=90)&(d['pred_funding_z'].abs()>1.0)).values
    pof10=((d['mins_to_funding']>=390)&(d['funding_last_z'].abs()>1.0)).values
    pfz2=(d['pred_funding_z'].abs()>2).values
    cbus=((d['xa_cb_prem_z7d'].abs()>2)&(d['sess_us_cash']==1)).values
    prem=(d['prem_z96'].abs()>2).values
    G['F_tight']=cascade|oifl|pf15|pof15                 # strict forced-flow
    G['F_wide']=cascade|oifl|pf10|pof10                  # widened funding windows
    G['F_pfz']=cascade|oifl|pf15|pof15|pfz2              # + extreme pred funding anywhere
    G['F_cb']=cascade|oifl|pf15|pof15|cbus               # + coinbase US
    G['F_pfz_cb']=G['F_pfz']|cbus
    G['U1']=cascade|oifl|prem|pf15|cbus
    return G

GA=gates(dfa)
WB=[1754006400000+i*2188800000 for i in range(13)]
ota=dfa['open_time'].values
print('=== freq per chaos test window (full data) ===')
for g,U in GA.items():
    fr=[U[(ota>=WB[w])&(ota<WB[w+1])].mean()*100 for w in range(12)]
    print(f'{g:10s} overall={U.mean()*100:5.1f}% min_win={min(fr):5.1f}% max_win={max(fr):5.1f}%')

c=df['close'].values.astype(float); ot=df['open_time'].values.astype(np.int64)
fwd=np.full(len(c),np.nan); fwd[:-K]=c[K:]/c[:-K]-1
y=(fwd>0).astype(float)
cols=[x for x in df.columns if x not in ('open_time','close')]
X=df[cols].values.astype(np.float64)
valid=df[cols].notna().all(axis=1).values & ~np.isnan(fwd)
G=gates(df); G['ungated']=np.ones(len(df),bool)
bounds=[CUTOFF-(6-i)*WLEN for i in range(7)]
COVS=[0.10,0.05,0.025]
res={g:{cv:[] for cv in COVS} for g in G}
for w in range(6):
    ws,we=bounds[w],bounds[w+1]
    te=np.where(valid&(ot>=ws)&(ot<we))[0]
    tr=np.where(valid&(ot<ws-EMB*300000))[0]
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
    bc=np.abs(p_cal-0.5); bt=np.abs(p_te-0.5)
    for g,act in G.items():
        # guard against degenerate threshold
        s_cal=np.where(act[cal_idx],bc,-1.0); s_te=np.where(act[te],bt,-1.0)
        for cv in COVS:
            thr=np.quantile(s_cal,1-cv)
            if thr<0: res[g][cv].append((-1,np.nan)); continue
            sel=s_te>=thr; n=int(sel.sum())
            hit=float(((p_te[sel]>0.5).astype(int)==yte[sel]).mean()) if n else np.nan
            res[g][cv].append((n,hit))
    print('win',w,'done')
print('\n=== pooled over 6 pre-windows (n=-1 means DEGENERATE threshold) ===')
for g in G:
    line=f'{g:10s}'
    for cv in COVS:
        rows=res[g][cv]
        if any(n==-1 for n,_ in rows): line+=f'  cov{cv}: DEGEN'; continue
        N=sum(n for n,_ in rows); H=sum(n*h for n,h in rows if n)/N
        minw=min(n for n,_ in rows); nw=sum(1 for n,h in rows if n>=30 and h>0.5)
        line+=f'  cov{cv}: n={N:5d} hit={H:.4f} minN={minw:4d} w>50:{nw}/6'
    print(line)
