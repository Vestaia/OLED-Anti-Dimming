// SPDX-License-Identifier: GPL-3.0-only
namespace OledCalibration;

// Reference-normalized circular HSV geometry. Four coordinates decouple saturation
// radius from hue displacement. State includes exact non-black coverage.
static class ScaledColorClusters
{
    public const int Count = 8, Dimensions = 49, Iterations = 8;
    public static readonly double BrightnessWeight = 100 / (Math.Sqrt(350) - Math.Sqrt(250));
    public readonly record struct Pixel(double X, double Y, double C, double Z, double Weight, double Active);
    public static readonly double AreaWeight = 100 - BrightnessWeight*BrightnessWeight*.025;
    public static Pixel Map(double[] rgb, double weight)
    {
        double r=Math.Max(rgb[0],0),g=Math.Max(rgb[1],0),b=Math.Max(rgb[2],0);
        double v=Math.Max(r,Math.Max(g,b)),d=v-Math.Min(r,Math.Min(g,b)),s=v>0?d/v:0;
        double h=d==0?0:v==r?(g-b)/d:v==g?2+(b-r)/d:4+(r-g)/d;
        h=h/6-Math.Floor(h/6);double a=h*2*Math.PI;
        return new(s*s*Math.Cos(a)/Math.Sqrt(3),s*s*Math.Sin(a)/Math.Sqrt(3),s*s*Math.Sqrt(2.0/3),BrightnessWeight*Math.Sqrt(Math.Min(v/40,1)),weight,v*250>.01?1:0);
    }
    static double Squared(Pixel p, double[] c) => (p.X-c[0])*(p.X-c[0])+(p.Y-c[1])*(p.Y-c[1])+(p.C-c[2])*(p.C-c[2])+(p.Z-c[3])*(p.Z-c[3]);
    public static double[] Fit(IEnumerable<Pixel> input)
    {
        var pixels=input.Where(p=>p.Weight>0).ToArray();
        if(pixels.Length==0)throw new InvalidDataException("Empty scene distribution");
        var centers=Enumerable.Range(0,Count).Select(_=>new double[4]).ToArray();
        // Actual-scene farthest-point seeds, starting at black. Lexical ties keep
        // spatial rearrangements and input enumeration order irrelevant.
        for(int k=1;k<Count;k++) {
            double best=-1;Pixel selected=default;
            foreach(var p in pixels) {
                double distance=double.PositiveInfinity;for(int j=0;j<k;j++)distance=Math.Min(distance,Squared(p,centers[j]));
                if(distance>best || distance==best && (p.X>selected.X || p.X==selected.X && (p.Y>selected.Y || p.Y==selected.Y && (p.C>selected.C || p.C==selected.C && p.Z>selected.Z)))) { best=distance;selected=p; }
            }
            centers[k]=[selected.X,selected.Y,selected.C,selected.Z];
        }
        int Winner(Pixel p) {int winner=0;double best=double.PositiveInfinity;for(int j=0;j<Count;j++){double d=Squared(p,centers[j]);if(d<best){best=d;winner=j;}}return winner;}
        for(int iteration=0;iteration<Iterations;iteration++) {
            var sums=new double[Count,5];
            foreach(var p in pixels){int j=Winner(p);sums[j,0]+=p.X*p.Weight;sums[j,1]+=p.Y*p.Weight;sums[j,2]+=p.C*p.Weight;sums[j,3]+=p.Z*p.Weight;sums[j,4]+=p.Weight;}
            for(int j=0;j<Count;j++)if(sums[j,4]>0)for(int a=0;a<4;a++)centers[j][a]=sums[j,a]/sums[j,4];
        }
        var population=new double[Count];var variance=new double[Count];
        foreach(var p in pixels){int j=Winner(p);population[j]+=p.Weight;variance[j]+=p.Weight*Squared(p,centers[j]);}
        double total=population.Sum();var result=new double[Dimensions];
        var order=Enumerable.Range(0,Count).OrderBy(j=>centers[j][0]).ThenBy(j=>centers[j][1]).ThenBy(j=>centers[j][2]).ThenBy(j=>centers[j][3]).ToArray();
        for(int k=0;k<Count;k++){int j=order[k];for(int a=0;a<4;a++)result[k*6+a]=centers[j][a];result[k*6+4]=population[j]/total;result[k*6+5]=population[j]>0?Math.Sqrt(variance[j]/population[j]):0;}
        result[48]=pixels.Sum(p=>p.Weight*p.Active)/total;return result;
    }
    public static double[] Black() {var state=new double[Dimensions];state[4]=1;return state;}
    // Symmetric greedy transport between mixture components. Gaussian component
    // Squared total transported distance plus squared coverage difference: intentionally
    // not Wasserstein distance. The 10% white-window reference has unit cost.
    public static double Distance(double[] a,double[] b)
    {
        double Directed(double[] x,double[] y) {
            var left=new double[Count];var right=new double[Count];var costs=new double[Count,Count];
            for(int i=0;i<Count;i++){left[i]=x[i*6+4];right[i]=y[i*6+4];for(int j=0;j<Count;j++){double cost=0;for(int axis=0;axis<4;axis++){double d=x[i*6+axis]-y[j*6+axis];cost+=d*d;}double spread=x[i*6+5]-y[j*6+5];costs[i,j]=cost+spread*spread;}}
            double sum=0;
            for(int transfer=0;transfer<15;transfer++) {
                double best=double.PositiveInfinity;int ii=-1,jj=-1;
                for(int i=0;i<Count;i++)if(left[i]>1e-12)for(int j=0;j<Count;j++)if(right[j]>1e-12&&costs[i,j]<best){best=costs[i,j];ii=i;jj=j;}
                if(ii<0)break;double mass=Math.Min(left[ii],right[jj]);sum+=mass*Math.Sqrt(best);left[ii]-=mass;right[jj]-=mass;
            }
            return sum;
        }
        // Canonicalize callers too, so arbitrary slot permutations cannot affect ties.
        double[] Canonical(double[] s)=>Enumerable.Range(0,Count).OrderBy(i=>s[i*6]).ThenBy(i=>s[i*6+1]).ThenBy(i=>s[i*6+2]).ThenBy(i=>s[i*6+3]).ThenBy(i=>s[i*6+5]).ThenBy(i=>s[i*6+4]).SelectMany(i=>s.Skip(i*6).Take(6)).ToArray();
        var ca=Canonical(a);var cb=Canonical(b);double area=a[48]-b[48];
        double transport=(Directed(ca,cb)+Directed(cb,ca))*.5;
        return transport*transport+AreaWeight*area*area;
    }
}
