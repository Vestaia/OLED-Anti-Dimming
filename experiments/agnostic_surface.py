"""Agnostic signal surfaces. Camera codes are never converted to luminance."""
import csv, json, argparse
from pathlib import Path
import numpy as np
from scipy.interpolate import BSpline, RBFInterpolator


def load(path):
    rows=list(csv.DictReader(Path(path).open()))
    good=set()
    for r in rows:
        if r['role']=='end_reference' and abs(float(r['camera_code'])-float(r['reference_code']))<=2:
            good.add(r['name'])
    selected=[r for r in rows if r['name'] in good and r['role']=='matched']
    x=np.array([[float(r[k]) for k in ('area','hue','saturation')] for r in selected])
    y=np.log([float(r['signal_nits'])/100 for r in selected])
    return x,y,np.array([r['name'] for r in selected]),rows


def embed(x):
    a,h,s=np.asarray(x).T
    h=h%1
    return np.column_stack((a,s*np.cos(2*np.pi*h)*.6,s*np.sin(2*np.pi*h)*.6,s*.4))


def basis(x):
    a,h,s=np.asarray(x).T
    knots=np.r_[[.01]*4,[.2,.4,.7],[1.]*4]
    A=BSpline.design_matrix(a,knots,3).toarray()-BSpline.design_matrix(np.full(len(a),.01),knots,3).toarray()
    # Fold extended uniform cubic B-spline coefficients onto a periodic ring.
    ext=BSpline.design_matrix(h%1,np.arange(-3,12)/8,3).toarray()
    H=np.zeros((len(a),8))
    for j in range(ext.shape[1]): H[:,j%8]+=ext[:,j]
    H=s[:,None]*H+(1-s[:,None])/8
    S=np.column_stack(((1-s)**3,3*s*(1-s)**2,3*s*s*(1-s),s**3))
    return np.einsum('ni,nj,nk->nijk',A,S,H).reshape(len(a),-1)


class Surface:
    def __init__(self,x,y,kind='spline',smooth=.03,boost_only=True):
        self.x=np.asarray(x);self.y=np.asarray(y);self.kind=kind;self.smooth=smooth;self.boost_only=boost_only
        if kind=='spline':
            B=basis(x);shape=(7,4,8);identity=np.eye(np.prod(shape)).reshape((*shape,-1))
            penalties=[np.diff(identity,n=2,axis=i).reshape(-1,B.shape[1]) for i in (0,1)]
            penalties.append((np.roll(identity,1,axis=2)-2*identity+np.roll(identity,-1,axis=2)).reshape(-1,B.shape[1]))
            P=np.vstack(penalties)
            self.coef=np.linalg.lstsq(np.vstack((B,np.sqrt(smooth)*P,.003*np.eye(B.shape[1]))),np.r_[y,np.zeros(P.shape[0]+B.shape[1])],rcond=None)[0]
        else:
            base=np.array([[.01,h,s] for _,h,s in x])
            xx=np.vstack((x,base));yy=np.r_[y,np.zeros(len(base))]
            xx,indices=np.unique(embed(xx),axis=0,return_index=True)
            self.rbf=RBFInterpolator(xx,yy[indices],kernel='thin_plate_spline',smoothing=smooth)
    def predict(self,x):
        x=np.atleast_2d(x)
        if self.kind=='spline': result=basis(x)@self.coef
        else:
            b=x.copy();b[:,0]=.01
            result=self.rbf(embed(x))-self.rbf(embed(b))
        return np.maximum(0,result) if self.boost_only else result
    def signal(self,h,s,a): return float(100*np.exp(np.clip(self.predict([[a,h,s]])[0],-2,3)))
    def save(self,path):
        Path(path).write_text(json.dumps(dict(kind=self.kind,smooth=self.smooth,boost_only=self.boost_only,x=self.x.tolist(),y=self.y.tolist(),scope='100 nominal signal; uniform colored windows; no mixed-scene validation'),indent=2),encoding='utf-8')
    @classmethod
    def read(cls,path):
        d=json.loads(Path(path).read_text(encoding='utf-8'));return cls(d['x'],d['y'],d['kind'],d['smooth'],d.get('boost_only',True))


def compare(x,y,groups):
    results=[]
    # Whole-color holdout prevents nearby points on the same sweep leaking into training.
    folds=[np.isin(groups,np.unique(groups)[i::4]) for i in range(4)]
    for kind in ('spline','rbf'):
        for smooth in (.001,.01,.1):
            residual=[]
            for hold in folds:
                m=Surface(x[~hold],y[~hold],kind,smooth)
                residual.extend(m.predict(x[hold])-y[hold])
            results.append(dict(kind=kind,smooth=smooth,log_signal_rmse=float(np.sqrt(np.mean(np.square(residual))))))
    return sorted(results,key=lambda r:r['log_signal_rmse'])

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('observations');p.add_argument('output');args=p.parse_args()
    x,y,g,rows=load(args.observations);scores=compare(x,y,g);best=scores[0]
    Surface(x,y,best['kind'],best['smooth']).save(args.output)
    Path(args.output).with_suffix('.comparison.json').write_text(json.dumps(scores,indent=2),encoding='utf-8')
    print(json.dumps(scores,indent=2))
