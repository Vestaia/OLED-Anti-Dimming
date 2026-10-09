// SPDX-License-Identifier: GPL-3.0-only
namespace OledCalibration;
static class ScaledClusterChecks {
    static double[] Solid(double[] rgb)=>ScaledColorClusters.Fit([ScaledColorClusters.Map(rgb,1)]);
    static double[] Window(double area)=>ScaledColorClusters.Fit([ScaledColorClusters.Map([1,1,1],area),ScaledColorClusters.Map([0,0,0],1-area)]);
    public static void Run(string root) {
        var white=Solid([1,1,1]);var brighter=Solid([1.4,1.4,1.4]);var red=Solid([1,0,0]);var green=Solid([0,1,0]);
        foreach(double d in new[]{ScaledColorClusters.Distance(white,brighter),ScaledColorClusters.Distance(red,green),ScaledColorClusters.Distance(white,red),ScaledColorClusters.Distance(Window(.4),Window(.5))})if(Math.Abs(d-1)>1e-10)throw new Exception("Reference-normalized distances differ: "+d);
        if(Math.Abs(ScaledColorClusters.Distance(Window(.4),Window(.6))-4)>1e-10 || Math.Abs(ScaledColorClusters.Distance(Window(.4),Window(.7))-9)>1e-10)throw new Exception("Window scaling is not quadratic");
        if(ScaledColorClusters.Distance(Solid([.4,.4,.4]),Solid([.44,.44,.44]))<=ScaledColorClusters.Distance(Solid([.8,.8,.8]),Solid([.84,.84,.84])))throw new Exception("Square-root brightness shaping changed");
        if(ScaledColorClusters.Distance(Solid([1,1,1]),Solid([1,.9,.9]))>=ScaledColorClusters.Distance(Solid([1,.1,.1]),red))throw new Exception("Quadratic saturation shaping changed");
        var split=(double[])white.Clone();split[10]=.5;Array.Copy(split,6,split,12,6);
        if(Math.Abs(ScaledColorClusters.Distance(white,green)-ScaledColorClusters.Distance(split,green))>1e-10)throw new Exception("Splitting identical population changes distance");
        var reversed=Enumerable.Range(0,8).Reverse().SelectMany(i=>red.Skip(i*6).Take(6)).Append(red[48]).ToArray();if(ScaledColorClusters.Distance(red,reversed)>1e-12)throw new Exception("Scaled distance depends on cluster order");
        var unique=CalibrationModel.FromScaledClusters([white,red],[.1,.2]);var duplicate=CalibrationModel.FromScaledClusters([white,red,white],[.1,.2,.1]);if(duplicate.Centers.Length!=unique.Centers.Length || Math.Abs(unique.Predict(brighter)-duplicate.Predict(brighter))>1e-12)throw new Exception("Duplicate state changes prediction");
        var averaged=CalibrationModel.FromScaledClusters([white,white],[.1,.3]);if(Math.Abs(averaged.Predict(white)-.2)>1e-9)throw new Exception("Duplicate gains not averaged");
        double[][] palette={ [.4,.4,.4],[4,4,4],[.1,.1,.1],[4,0,0],[0,4,0],[0,0,4],[4,0,4],[.2,.4,.8] };
        var states=palette.Select(Solid).Append(Window(.4)).Append(Window(.7)).ToArray();var model=CalibrationModel.FromScaledClusters(states,Enumerable.Range(0,states.Length).Select(i=>i*.03).ToArray());
        var folder=Path.Combine(root,"build","scaled-cluster-test-config");Directory.CreateDirectory(folder);var json=Path.Combine(folder,"runtime-model.json");File.WriteAllText(json,model.Json(""));var loaded=CalibrationModel.Load(json);if(!loaded.ScaledClusters||Math.Abs(loaded.Predict(brighter)-model.Predict(brighter))>1e-12)throw new Exception("Scaled model serialization changed prediction");Backend.WriteModel(json,Path.Combine(folder,"runtime.bin"));
        using var expected=new BinaryWriter(File.Create(Path.Combine(folder,"expected.bin")));foreach(var rgb in palette){foreach(double v in rgb)expected.Write((float)(v*250));expected.Write((float)Math.Exp(model.Predict(Solid(rgb))));}
        var state=ScaledColorClusters.Fit(Enumerable.Range(0,600).Select(i=>ScaledColorClusters.Map(palette[i%8],1.0/600)));
        using(var windows=new BinaryWriter(File.Create(Path.Combine(folder,"windows.bin"))))foreach(double area in new[]{.4,.5,.6,.7})windows.Write((float)Math.Exp(model.Predict(Window(area))));
        using var mix=new BinaryWriter(File.Create(Path.Combine(folder,"mixture.bin")));foreach(double v in state)mix.Write(v);mix.Write(Math.Exp(model.Predict(state)));
        Console.WriteLine("PASS: equal reference distances, quadratic window scaling, brightness/saturation shaping, duplicate invariance, scaled model round trip");
    }
}
