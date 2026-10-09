// SPDX-License-Identifier: GPL-3.0-only
namespace OledCalibration;

// Eight weighted clusters in circular HSV geometry. No learned PCA transform.
static class ColorClusters
{
    public const int Count = 8, Dimensions = 40, Iterations = 8;
    public const double BrightnessWeight = 10;
    public readonly record struct Pixel(double X, double Y, double Z, double Weight);
    public static Pixel Map(double[] rgb, double weight)
    {
        double r=Math.Max(rgb[0],0),g=Math.Max(rgb[1],0),b=Math.Max(rgb[2],0);
        double v=Math.Max(r,Math.Max(g,b)),d=v-Math.Min(r,Math.Min(g,b)),s=v>0?d/v:0;
        double h=d==0?0:v==r?(g-b)/d:v==g?2+(b-r)/d:4+(r-g)/d;
        h=h/6-Math.Floor(h/6);double a=h*2*Math.PI;
        return new(s*s*Math.Cos(a),s*s*Math.Sin(a),BrightnessWeight*Math.Sqrt(Math.Min(v/40,1)),weight);
    }
    static double Squared(Pixel p, double[] c) => (p.X-c[0])*(p.X-c[0])+(p.Y-c[1])*(p.Y-c[1])+(p.Z-c[2])*(p.Z-c[2]);
    public static double[] Fit(IEnumerable<Pixel> input)
    {
        var pixels=input.Where(p=>p.Weight>0).ToArray();
        if(pixels.Length==0)throw new InvalidDataException("Empty scene distribution");
        var centers=Enumerable.Range(0,Count).Select(_=>new double[3]).ToArray();
        // Actual-scene farthest-point seeds, starting at black. Lexical ties keep
        // spatial rearrangements and input enumeration order irrelevant.
        for(int k=1;k<Count;k++) {
            double best=-1;Pixel selected=default;
            foreach(var p in pixels) {
                double distance=double.PositiveInfinity;for(int j=0;j<k;j++)distance=Math.Min(distance,Squared(p,centers[j]));
                if(distance>best || distance==best && (p.X>selected.X || p.X==selected.X && (p.Y>selected.Y || p.Y==selected.Y && p.Z>selected.Z))) { best=distance;selected=p; }
            }
            centers[k]=[selected.X,selected.Y,selected.Z];
        }
        int Winner(Pixel p) {int winner=0;double best=double.PositiveInfinity;for(int j=0;j<Count;j++){double d=Squared(p,centers[j]);if(d<best){best=d;winner=j;}}return winner;}
        for(int iteration=0;iteration<Iterations;iteration++) {
            var sums=new double[Count,4];
            foreach(var p in pixels){int j=Winner(p);sums[j,0]+=p.X*p.Weight;sums[j,1]+=p.Y*p.Weight;sums[j,2]+=p.Z*p.Weight;sums[j,3]+=p.Weight;}
            for(int j=0;j<Count;j++)if(sums[j,3]>0)for(int a=0;a<3;a++)centers[j][a]=sums[j,a]/sums[j,3];
        }
        var population=new double[Count];var variance=new double[Count];
        foreach(var p in pixels){int j=Winner(p);population[j]+=p.Weight;variance[j]+=p.Weight*Squared(p,centers[j]);}
        double total=population.Sum();var result=new double[Dimensions];
        var order=Enumerable.Range(0,Count).OrderBy(j=>centers[j][0]).ThenBy(j=>centers[j][1]).ThenBy(j=>centers[j][2]).ToArray();
        for(int k=0;k<Count;k++){int j=order[k];for(int a=0;a<3;a++)result[k*5+a]=centers[j][a];result[k*5+3]=population[j]/total;result[k*5+4]=population[j]>0?Math.Sqrt(variance[j]/population[j]):0;}
        return result;
    }
    public static double[] Black() {var state=new double[Dimensions];state[3]=1;return state;}
    // Symmetric greedy transport between mixture components. Gaussian component
    // cost includes RMS spread; this is an approximation, not optimal transport.
    public static double Distance(double[] a,double[] b)
    {
        double Directed(double[] x,double[] y) {
            var left=new double[Count];var right=new double[Count];var costs=new double[Count,Count];
            for(int i=0;i<Count;i++){left[i]=x[i*5+3];right[i]=y[i*5+3];for(int j=0;j<Count;j++){double cost=0;for(int axis=0;axis<3;axis++){double d=x[i*5+axis]-y[j*5+axis];cost+=d*d;}double spread=x[i*5+4]-y[j*5+4];costs[i,j]=cost+spread*spread;}}
            double sum=0;
            for(int transfer=0;transfer<15;transfer++) {
                double best=double.PositiveInfinity;int ii=-1,jj=-1;
                for(int i=0;i<Count;i++)if(left[i]>1e-12)for(int j=0;j<Count;j++)if(right[j]>1e-12&&costs[i,j]<best){best=costs[i,j];ii=i;jj=j;}
                if(ii<0)break;double mass=Math.Min(left[ii],right[jj]);sum+=mass*best;left[ii]-=mass;right[jj]-=mass;
            }
            return sum;
        }
        // Canonicalize callers too, so arbitrary slot permutations cannot affect ties.
        double[] Canonical(double[] s)=>Enumerable.Range(0,Count).OrderBy(i=>s[i*5]).ThenBy(i=>s[i*5+1]).ThenBy(i=>s[i*5+2]).ThenBy(i=>s[i*5+4]).ThenBy(i=>s[i*5+3]).SelectMany(i=>s.Skip(i*5).Take(5)).ToArray();
        var ca=Canonical(a);var cb=Canonical(b);return (Directed(ca,cb)+Directed(cb,ca))*.5;
    }
}
