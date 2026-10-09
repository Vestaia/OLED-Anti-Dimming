// SPDX-License-Identifier: GPL-3.0-only
namespace OledCalibration;
static class ClusterChecks {
    public static void ExportLive(string json,string folder) {
        Directory.CreateDirectory(folder);var model=CalibrationModel.Load(json);Backend.WriteModel(json,Path.Combine(folder,"runtime.bin"));
        double[][] palette={ [.4,.4,.4],[4,4,4],[.1,.1,.1],[4,0,0],[0,4,0],[0,0,4],[4,0,4],[.2,.4,.8] };
        using var expected=new BinaryWriter(File.Create(Path.Combine(folder,"expected.bin")));
        foreach(var rgb in palette){foreach(double value in rgb)expected.Write((float)(value*250));expected.Write((float)Math.Exp(model.Predict(model.TransportModel?ScaledColorClusters.Fit([CubeTransport.Map(rgb,1)]):model.ScaledClusters?ScaledColorClusters.Fit([ScaledColorClusters.Map(rgb,1)]):ColorClusters.Fit([ColorClusters.Map(rgb,1)]))));}
        var state=model.TransportModel?ScaledColorClusters.Fit(Enumerable.Range(0,600).Select(i=>CubeTransport.Map(palette[i%8],1.0/600))):model.ScaledClusters?ScaledColorClusters.Fit(Enumerable.Range(0,600).Select(i=>ScaledColorClusters.Map(palette[i%8],1.0/600))):ColorClusters.Fit(Enumerable.Range(0,600).Select(i=>ColorClusters.Map(palette[i%8],1.0/600)));
        using var mixture=new BinaryWriter(File.Create(Path.Combine(folder,"mixture.bin")));foreach(double value in state)mixture.Write(value);mixture.Write(Math.Exp(model.Predict(state)));
    }
    public static void Run(string root) {
        ScaledClusterChecks.Run(root);GaussianKernelChecks.Run(root);CubeTransportChecks.Run(root);
        var white=ColorClusters.Fit([ColorClusters.Map([.4,.4,.4],1)]);
        var dark=ColorClusters.Fit([ColorClusters.Map([.1,.1,.1],1)]);
        if(Math.Abs(ColorClusters.Distance(white,dark)-.25)>1e-10)throw new Exception("Cluster brightness weighting changed");
        var mixed=ColorClusters.Fit(new double[][]{[1.2,0,0],[0,1.2,0],[0,0,1.2]}.Select(c=>ColorClusters.Map(c,1.0/3)));
        if(ColorClusters.Distance(white,mixed)<.1)throw new Exception("Colored mixture collapsed to white");
        var permuted=Enumerable.Range(0,8).Reverse().SelectMany(i=>mixed.Skip(i*5).Take(5)).ToArray();
        if(ColorClusters.Distance(mixed,permuted)>1e-12 || Math.Abs(ColorClusters.Distance(white,mixed)-ColorClusters.Distance(mixed,white))>1e-12)throw new Exception("Cluster comparison is not symmetric/permutation independent");
        var point=ColorClusters.Map([1,0,0],1);var seam=ColorClusters.Map([1,0,.000001],1);if(Math.Abs(point.X-seam.X)+Math.Abs(point.Y-seam.Y)>1e-5)throw new Exception("Cluster hue seam discontinuity");
        var folder=Path.Combine(root,"build","cluster-test-config");Directory.CreateDirectory(folder);
        double[][] palettes={ [.4,.4,.4],[4,4,4],[.1,.1,.1],[4,0,0],[0,4,0],[0,0,4],[4,0,4],[.2,.4,.8] };
        var states=palettes.Select(c=>ColorClusters.Fit([ColorClusters.Map(c,1)])).ToArray();
        var model=CalibrationModel.FromClusters(states,Enumerable.Range(0,8).Select(i=>.03*i).ToArray());
        var path=Path.Combine(folder,"runtime-model.json");File.WriteAllText(path,model.Json(""));Backend.WriteModel(path,Path.Combine(folder,"runtime.bin"));
        var loaded=CalibrationModel.Load(path);if(!loaded.ClusterModel || Math.Abs(loaded.Predict(white)-model.Predict(white))>1e-12)throw new Exception("Cluster model round trip failed");
        var mosaic=new CalibrationScene{area=1,moments=ManagedCalibration.Moments([[.4,.4,.4]]),mosaic_probe=true,display_width=320,display_height=180};
        var solid=new CalibrationScene{area=1,moments=mosaic.moments,mosaic_probe=false,display_width=320,display_height=180};
        if(ColorClusters.Distance(mosaic.Clusters(7),solid.Clusters(7))<=1e-8)throw new Exception("Exact center mosaic was omitted");
        using var expected=new BinaryWriter(File.Create(Path.Combine(folder,"expected.bin")));
        foreach(var rgb in palettes){foreach(double value in rgb)expected.Write((float)(value*250));expected.Write((float)Math.Exp(model.Predict(model.TransportModel?ScaledColorClusters.Fit([CubeTransport.Map(rgb,1)]):model.ScaledClusters?ScaledColorClusters.Fit([ScaledColorClusters.Map(rgb,1)]):ColorClusters.Fit([ColorClusters.Map(rgb,1)]))));}
        // Broad, unequal-population case, for the GPU fit and spread check.
        var pixels=Enumerable.Range(0,600).Select(i=>ColorClusters.Map(palettes[i%8],1.0/600)).ToArray();var state=ColorClusters.Fit(pixels);
        using var mixture=new BinaryWriter(File.Create(Path.Combine(folder,"mixture.bin")));foreach(double value in state)mixture.Write(value);mixture.Write(Math.Exp(model.Predict(state)));
        Console.WriteLine("PASS: cluster geometry, hue wrap, mixtures, population, permutation, serialization, exact probe");
    }
}
