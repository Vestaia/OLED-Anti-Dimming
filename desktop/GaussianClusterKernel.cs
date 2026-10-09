// SPDX-License-Identifier: GPL-3.0-only
namespace OledCalibration;

// Version 9 keeps version 8's k-means states. RMS spread is represented as
// isotropic covariance (trace / four axes), not fitted with expensive EM.
static class GaussianClusterKernel
{
    sealed record SelfSimilarity(double Value);
    // Model states are immutable after fitting; weak keys avoid retaining old runs.
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<double[],SelfSimilarity> Self=new();
    public static readonly double Normalization=2*(1-Math.Exp(-.5));
    public static readonly double LegacyAreaWeight=100-2*(1-Math.Exp(-ScaledColorClusters.BrightnessWeight*ScaledColorClusters.BrightnessWeight*.025/2))/Normalization;
    public static double Similarity(double[] a,double[] b)
    {
        double sum=0;
        for(int i=0;i<8;i++) if(a[i*6+4]>0)
            for(int j=0;j<8;j++) if(b[j*6+4]>0) {
                double q=1+(a[i*6+5]*a[i*6+5]+b[j*6+5]*b[j*6+5])/4;
                double squared=0;for(int axis=0;axis<4;axis++){double d=a[i*6+axis]-b[j*6+axis];squared+=d*d;}
                sum+=a[i*6+4]*b[j*6+4]*Math.Exp(-squared/(2*q))/(q*q);
            }
        return sum;
    }
    public static double Distance(double[] a,double[] b,bool legacyCoverage=false)
    {
        double coverage=a[48]-b[48];
        return Math.Max(0,Self.GetValue(a,v=>new(Similarity(v,v))).Value+Self.GetValue(b,v=>new(Similarity(v,v))).Value-2*Similarity(a,b))/Normalization+(legacyCoverage?LegacyAreaWeight*coverage*coverage:0);
    }
}
