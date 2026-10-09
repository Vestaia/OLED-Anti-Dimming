// SPDX-License-Identifier: GPL-3.0-only
namespace OledCalibration;

// Population preserving, separable Laplace smoothing in H, S², sqrt(V).
static class SmoothedHsv
{
    public const int H = 12, S = 6, V = 16, Dimensions = H*S*V;
    public const double Bandwidth = .005;
    public static readonly double[] Smoothing = [1.2,30.0/11,10.0/3];
    public static readonly double[][] Matrices = [Matrix(H,1.2,true),Matrix(S,30.0/11,false),Matrix(V,10.0/3,false)];
    static double[] Matrix(int n,double width,bool circular)
    {
        var m=new double[n*n];
        for(int source=0;source<n;source++) {
            double total=0;
            for(int dest=0;dest<n;dest++) { int delta=Math.Abs(dest-source);if(circular)delta=Math.Min(delta,n-delta);total+=m[dest*n+source]=Math.Exp(-delta/width); }
            for(int dest=0;dest<n;dest++)m[dest*n+source]/=total;
        }
        return m;
    }
    public static void Add(double[] bins,double r,double g,double b,double population,double peak)
    {
        double value=Math.Max(r,Math.Max(g,b)),minimum=Math.Min(r,Math.Min(g,b)),delta=value-minimum;
        double saturation=value>0?delta/value:0,hue=0;
        if(delta>0) { hue=(value==r?(g-b)/delta:value==g?(b-r)/delta+2:(r-g)/delta+4)/6;hue-=Math.Floor(hue); }
        double hc=hue*H,sc=saturation*saturation*(S-1),vc=Math.Sqrt(Math.Clamp(value*250/peak,0,1))*(V-1);
        int hi=(int)hc,si=(int)sc,vi=(int)vc;
        double hf=hc-hi,sf=sc-si,vf=vc-vi,chromatic=saturation*saturation;
        for(int ds=0;ds<2;ds++)for(int dv=0;dv<2;dv++) {
            double weight=population*(ds==0?1-sf:sf)*(dv==0?1-vf:vf);
            int index=(Math.Min(S-1,si+ds)*V)+Math.Min(V-1,vi+dv);
            for(int h=0;h<H;h++)bins[h*S*V+index]+=weight*(1-chromatic)/H;
            bins[hi*S*V+index]+=weight*chromatic*(1-hf);
            bins[((hi+1)%H)*S*V+index]+=weight*chromatic*hf;
        }
    }
    static readonly Dictionary<(int,int,bool,double),double[]> probeCache=new(),zeroCache=new();
    public static double[] Encode(CalibrationScene scene,double peak)
    {
        if(scene.grid_hue>=0 && scene.grid_brightness==0) {
            var zeroKey=(scene.display_width,scene.display_height,scene.mosaic_probe,peak);
            lock(zeroCache) {
                if(!zeroCache.TryGetValue(zeroKey,out var zero))zeroCache[zeroKey]=zero=Encode(new CalibrationScene {area=0,moments=new double[14],display_width=scene.display_width,display_height=scene.display_height,mosaic_probe=scene.mosaic_probe},peak);
                return zero;
            }
        }
        var raw=new double[Dimensions];
        foreach(var (color,population) in ManagedCalibration.SceneColors(scene,false))Add(raw,color.Item1,color.Item2,color.Item3,population,peak);
        var result=Smooth(raw);
        var key=(scene.display_width,scene.display_height,scene.mosaic_probe,peak);
        lock(probeCache) {
            if(!probeCache.TryGetValue(key,out var probe)) {
                var empty=new CalibrationScene {area=0,moments=new double[14],display_width=scene.display_width,display_height=scene.display_height,mosaic_probe=scene.mosaic_probe};
                var colors=ManagedCalibration.SceneColors(empty);
                foreach(var (c,w) in ManagedCalibration.SceneColors(empty,false))colors[c]-=w;
                var probeRaw=new double[Dimensions];foreach(var (c,w) in colors)if(w>0)Add(probeRaw,c.Item1,c.Item2,c.Item3,w,peak);
                probeCache[key]=probe=Smooth(probeRaw);
            }
            for(int i=0;i<Dimensions;i++)result[i]+=probe[i];
        }
        return result;
    }
    static double[] Smooth(double[] raw) => Transform(raw,Matrices);
    static double[] Transform(double[] raw,double[][] matrices) {
        for(int axis=0;axis<3;axis++) {
            int n=axis==0?H:axis==1?S:V,stride=axis==0?S*V:axis==1?V:1;
            var output=new double[Dimensions];var m=matrices[axis];
            for(int index=0;index<Dimensions;index++) {
                int dest=(index/stride)%n,start=index-dest*stride;double sum=0;
                for(int source=0;source<n;source++)sum+=raw[start+source*stride]*m[dest*n+source];
                output[index]=sum;
            }
            raw=output;
        }
        return raw;
    }
    // Re-express saved smoothed populations: new matrix * inverse(old matrix).
    // Each axis is small; corrections and original calibration files are untouched.
    public static double[][] Remap(double[][] states,double[] widths,int[]? shape=null) {
        shape ??= [H,S,V];
        if(shape.Length!=3 || shape[0]!=H || shape[1]<2 || shape[2]<2 || states.Any(a=>a.Length!=shape[0]*shape[1]*shape[2]))throw new InvalidDataException("Invalid source histogram shape");
        if(widths.Length!=3 || widths.Any(w=>!double.IsFinite(w)||w<=0))throw new InvalidDataException("Invalid histogram smoothing");
        if(widths.SequenceEqual(Smoothing) && shape.SequenceEqual(new[]{H,S,V}))return states;
        var transforms=new double[3][];
        for(int axis=0;axis<3;axis++) {
            int target=axis==0?H:axis==1?S:V,n=shape[axis];
            var old=Matrix(n,widths[axis],axis==0);var augmented=new double[n,2*n];
            for(int row=0;row<n;row++)for(int col=0;col<n;col++){augmented[row,col]=old[row*n+col];augmented[row,n+col]=row==col?1:0;}
            for(int col=0;col<n;col++) {
                int pivot=col;for(int row=col+1;row<n;row++)if(Math.Abs(augmented[row,col])>Math.Abs(augmented[pivot,col]))pivot=row;
                if(Math.Abs(augmented[pivot,col])<1e-14)throw new InvalidDataException("Histogram smoothing cannot be remapped");
                for(int j=0;j<2*n;j++){double temp=augmented[col,j];augmented[col,j]=augmented[pivot,j];augmented[pivot,j]=temp;}
                double divisor=augmented[col,col];for(int j=0;j<2*n;j++)augmented[col,j]/=divisor;
                for(int row=0;row<n;row++)if(row!=col){double factor=augmented[row,col];for(int j=0;j<2*n;j++)augmented[row,j]-=factor*augmented[col,j];}
            }
            // Map source grid populations to the target grid with linear splats.
            var rebin=new double[target*n];
            for(int source=0;source<n;source++) {
                double coordinate=axis==0?(double)source*target/n:(double)source*(target-1)/(n-1);
                int lo=(int)coordinate;double fraction=coordinate-lo;
                rebin[lo*n+source]+=1-fraction;
                rebin[(axis==0?(lo+1)%target:Math.Min(lo+1,target-1))*n+source]+=fraction;
            }
            var smoothed=new double[target*n];
            for(int row=0;row<target;row++)for(int col=0;col<n;col++)for(int k=0;k<target;k++)smoothed[row*n+col]+=Matrices[axis][row*target+k]*rebin[k*n+col];
            var transform=transforms[axis]=new double[target*n];
            for(int row=0;row<target;row++)for(int col=0;col<n;col++)for(int k=0;k<n;k++)transform[row*n+col]+=smoothed[row*n+k]*augmented[k,n+col];
        }
        return states.Select(state=> {
            var mapped=state;var current=(int[])shape.Clone();int[] targets=[H,S,V];
            for(int axis=0;axis<3;axis++) {
                int sourceCount=current[axis],targetCount=targets[axis],stride=axis==0?current[1]*current[2]:axis==1?current[2]:1;
                current[axis]=targetCount;var output=new double[current[0]*current[1]*current[2]];
                for(int index=0;index<output.Length;index++) {
                    int dest=(index/stride)%targetCount,start=(index/(stride*targetCount))*(stride*sourceCount)+index%stride;
                    double sum=0;for(int source=0;source<sourceCount;source++)sum+=mapped[start+source*stride]*transforms[axis][dest*sourceCount+source];
                    output[index]=sum;
                }
                mapped=output;
            }
            if(mapped.Any(v=>!double.IsFinite(v)||v < -1e-10))throw new InvalidDataException("Invalid remapped histogram");
            for(int i=0;i<mapped.Length;i++)mapped[i]=Math.Max(0,mapped[i]);
            double population=mapped.Sum();for(int i=0;i<mapped.Length;i++)mapped[i]/=population;
            return mapped;
        }).ToArray();
    }
    public static double DistanceSquared(double[] a,double[] b) {
        var total=System.Numerics.Vector<double>.Zero;int width=System.Numerics.Vector<double>.Count,i=0;
        for(;i+width<=Dimensions;i+=width){var delta=new System.Numerics.Vector<double>(a,i)-new System.Numerics.Vector<double>(b,i);total+=delta*delta;}
        double sum=System.Numerics.Vector.Sum(total);for(;i<Dimensions;i++){double delta=a[i]-b[i];sum+=delta*delta;}return sum*(Dimensions/9216.0);
    }
    public static double LogWeight(double d) {
        double ratio=Math.Abs(d)/Bandwidth;return -ratio*ratio*ratio;
    }
    public static double Predict(double[] state,double[][] centers,double[] gains) {
        double maximum=double.NegativeInfinity,total=0,sum=0;
        for(int i=0;i<centers.Length;i++) {
            double logWeight=LogWeight(Math.Sqrt(DistanceSquared(state,centers[i])));
            if(logWeight>maximum){double rescale=Math.Exp(maximum-logWeight);sum*=rescale;total*=rescale;maximum=logWeight;}
            double weight=Math.Exp(logWeight-maximum);sum+=weight*gains[i];total+=weight;
        }
        return total>0?Math.Max(0,sum/total):0;
    }
}
