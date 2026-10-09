// SPDX-License-Identifier: GPL-3.0-only
namespace OledCalibration;
static class GaussianKernelChecks
{
    static double[] Point(double z,double spread=0){var a=new double[49];a[3]=z;a[4]=1;a[5]=spread;a[48]=1;return a;}
    static void Near(double actual,double expected,double tolerance=1e-10){if(Math.Abs(actual-expected)>tolerance)throw new Exception($"Kernel distance {actual} != {expected}");}
    public static void Run(string root)
    {
        var a=Point(0);var b=Point(1);Near(GaussianClusterKernel.Distance(a,b),1);
        var split=Point(0);split[4]=.2;split[10]=.8;split[48]=1;
        Near(GaussianClusterKernel.Distance(a,split),0);
        var broad=Point(0,.2);var children=new double[49];children[3]=-.2;children[4]=.5;children[9]=.2;children[10]=.5;children[48]=1;
        if(GaussianClusterKernel.Distance(broad,children)>.001)throw new Exception("Equivalent broad/split distributions are too far apart");
        for(int i=1;i<20;i++)if(GaussianClusterKernel.Distance(a,Point(i*.2))<=GaussianClusterKernel.Distance(a,Point((i-1)*.2)))throw new Exception("Point kernel has secondary similarity peaks");
        var white=ScaledColorClusters.Map([1,1,1],1);
        double[] Window(double area)=>ScaledColorClusters.Fit([white with {Weight=area},ScaledColorClusters.Map([0,0,0],1-area)]);
        double unit=2*(1-Math.Exp(-white.Z*white.Z/2))/GaussianClusterKernel.Normalization;
        Near(GaussianClusterKernel.Distance(Window(.4),Window(.5)),.01*unit);
        Near(GaussianClusterKernel.Distance(Window(.4),Window(.6)),.04*unit);
        Near(GaussianClusterKernel.Distance(Window(.4),Window(.7)),.09*unit);
        var differentCoverage=(double[])a.Clone();differentCoverage[48]=0;Near(GaussianClusterKernel.Distance(a,differentCoverage),0);
        Console.WriteLine($"Distribution-only 40/60% white250nit: squared cost {.04*unit:R}, distance {Math.Sqrt(.04*unit):R}");
        Near(GaussianClusterKernel.Distance(broad,children),GaussianClusterKernel.Distance(children,broad));
        double[][] palette={ [.4,.4,.4],[4,4,4],[.1,.1,.1],[4,0,0],[0,4,0],[0,0,4],[4,0,4],[.2,.4,.8] };
        var states=palette.Select(rgb=>ScaledColorClusters.Fit([ScaledColorClusters.Map(rgb,1)])).ToArray();
        var model=CalibrationModel.FromGaussianClusters(states,Enumerable.Range(0,8).Select(i=>i*.03).ToArray());
        var folder=Path.Combine(root,"build","gaussian-cluster-test-config");Directory.CreateDirectory(folder);
        var path=Path.Combine(folder,"runtime-model.json");File.WriteAllText(path,model.Json(""));
        Near(CalibrationModel.Load(path).Predict(states[3]),model.Predict(states[3]));
        ClusterChecks.ExportLive(path,folder);
        using(var windows=new BinaryWriter(File.Create(Path.Combine(folder,"windows.bin"))))for(int i=0;i<4;i++)windows.Write((float)Math.Exp(model.Predict(Window(.4+.1*i))));
        Console.WriteLine("PASS: Gaussian kernel identity, equivalent splits, broad mixtures, monotonic point similarity, window scaling and model serialization");
    }
}
