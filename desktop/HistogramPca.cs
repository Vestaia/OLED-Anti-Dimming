namespace OledCalibration;

// Experimental distribution encoder. Coordinates are linear BT.2020 / 250 nits.
// Soft bins prevent abrupt changes at bin boundaries; sqrt allocates more bins
// to low signals without introducing perceptual weighting into the fit.
sealed class HistogramPca
{
    public const int Bins = 512, Components = 14;
    public double[][] Basis { get; set; } = [];
    public static void Add(double[] h, double[] c, double weight = 1)
    {
        var x = c.Select(v => Math.Clamp(Math.Sqrt(Math.Max(0, v) / 40) * 7, 0, 7)).ToArray();
        var lo = x.Select(v => Math.Min(6, (int)v)).ToArray();
        var t = x.Select((v,j) => v-lo[j]).ToArray();
        for(int k=0;k<8;k++) {
            int r=k&1,g=(k>>1)&1,b=(k>>2)&1;
            h[(lo[0]+r)*64+(lo[1]+g)*8+lo[2]+b] += weight*(r==0?1-t[0]:t[0])*(g==0?1-t[1]:t[1])*(b==0?1-t[2]:t[2]);
        }
    }
    public double[] Project(double[] h) => Basis.Select(v => v.Select((a,i)=>a*(h[i]-(i==0?1:0))).Sum()).ToArray();
    public static HistogramPca Fit(IEnumerable<double[]> input, double peakNits = 10000)
    {
        // Include synthetic distributions: basis learning requires no camera labels.
        var rows=input.ToList();
        foreach(double nits in new[]{25.0,50,75,100,150,200,250,350,500,750,1000,2500,10000,peakNits}.Where(v=>v<=peakNits).Distinct())
        foreach(var color in new double[][]{[1,1,1],[1,0,0],[0,1,0],[0,0,1],[1,1,0],[1,0,1],[0,1,1]}) {
            var h=new double[Bins];Add(h,color.Select(v=>v*nits/250).ToArray());rows.Add(h);
        }
        var rng=new Random(2084);
        for(int n=0;n<256;n++) {
            var h=new double[Bins]; int modes=1+n%5;
            for(int k=0;k<modes;k++) Add(h,Enumerable.Range(0,3).Select(_=>peakNits/250*Math.Pow(rng.NextDouble(),2)).ToArray(),1.0/modes);
            rows.Add(h);
        }
        var mean=Enumerable.Range(0,Bins).Select(i=>rows.Average(h=>h[i])).ToArray();
        var centered=rows.Select(h=>h.Select((v,i)=>v-mean[i]).ToArray()).ToArray();
        var basis=new List<double[]>();
        // Orthogonal power iteration on covariance, without allocating a 512? matrix.
        for(int c=0;c<Components;c++) {
            var v=Enumerable.Range(0,Bins).Select(_=>rng.NextDouble()-.5).ToArray();
            for(int iteration=0;iteration<24;iteration++) {
                var next=new double[Bins];
                foreach(var h in centered) {double dot=h.Zip(v,(a,b)=>a*b).Sum();for(int i=0;i<Bins;i++)next[i]+=h[i]*dot;}
                foreach(var old in basis) {double dot=next.Zip(old,(a,b)=>a*b).Sum();for(int i=0;i<Bins;i++)next[i]-=old[i]*dot;}
                double norm=Math.Sqrt(next.Sum(a=>a*a));
                if(norm<1e-12)throw new Exception("Insufficient histogram rank");
                v=next.Select(a=>a/norm).ToArray();
            }
            basis.Add(v);
        }
        return new HistogramPca {Basis=basis.ToArray()};
    }
}
