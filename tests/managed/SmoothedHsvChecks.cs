// SPDX-License-Identifier: GPL-3.0-only
namespace OledCalibration;
static partial class ManagedCalibration
{
    internal static void CheckHsvGrid(string folder) {
        var meta=HsvGridSeeds(folder,"test",1000,false,256,144);
        if(meta.has_white_subpixel || meta.scenes.Any(s=>s.prune_lower_saturation))throw new Exception("Saturation pruning must default off");
        var whitePanel=HsvGridSeeds(Path.Combine(folder,"white-panel"),"test",1000,true,256,144,true);
        if(!whitePanel.has_white_subpixel || whitePanel.scenes.Any(s=>!s.prune_lower_saturation))throw new Exception("White-subpixel setting did not reach the plan scenes");
        var nonzero=meta.scenes.Where(s=>s.grid_brightness>0).ToArray();
        if(nonzero.Length!=2590 || nonzero.Select(s=>s.grid_brightness).Distinct().Order().Zip(new[]{10.0,40,90,160,250,360,490,640,810,1000}).Any(p=>Math.Abs(p.First-p.Second)>1e-8))throw new Exception("HSV grid brightness schedule changed");
        if(meta.scenes.Count(s=>s.grid_brightness==0)!=259)throw new Exception("Missing V=0 grid anchors");
        if(nonzero.Select(s=>s.area).Distinct().Count()!=7 || nonzero.Where(s=>s.grid_saturation>0).Select(s=>s.grid_hue).Distinct().Count()!=9)throw new Exception("HSV grid coverage changed");
        var express=HsvGridSeeds(Path.Combine(folder,"express"),"test",1000,true,256,144);
        var expressColored=express.scenes.Where(s=>s.grid_brightness>0 && s.grid_saturation>0).ToArray();
        if(expressColored.Length!=480 || expressColored.Select(s=>s.grid_hue).Distinct().Order().SequenceEqual(new[]{0.0,60,120,180,240,300})==false || expressColored.Select(s=>s.grid_saturation).Distinct().Order().SequenceEqual(new[]{.5,1.0})==false || expressColored.Select(s=>s.area).Distinct().Order().SequenceEqual(new[]{.1,.25,.5,.75,1.0})==false)throw new Exception("Express HSV grid coverage changed");
        var levels=expressColored.Select(s=>s.grid_brightness).Distinct().Order().ToArray();
        if(levels.Length!=8 || levels.Select((v,i)=>Math.Abs(v-1000*Math.Pow((i+1)/8.0,2))).Any(error=>error>1e-8) || express.scenes.Count(s=>s.grid_brightness>0 && s.grid_saturation==0)!=40 || express.scenes.Count(s=>s.grid_brightness==0)!=65)throw new Exception("Express brightness, grayscale or V=0 coverage changed");
        var black=meta.scenes.Where(s=>s.grid_brightness==0).Take(8).Select(s=>s.SmoothState(1000)).ToArray();
        if(black.Any(s=>SmoothedHsv.DistanceSquared(s,black[0])>1e-20))throw new Exception("Black anchors depend on hue or area");
        if(CalibrationModel.FromSmoothHistogram(black,new double[black.Length],1000).Centers.Length!=1)throw new Exception("V=0 grid anchors were counted repeatedly");
        var seed=CalibrationModel.FromSmoothHistogram([black[0]],[0],1000);
        string plan=Path.Combine(folder,"plan.csv");
        Plan(plan,[nonzero[0],meta.scenes.Last()],seed,true);
        var lines=File.ReadAllLines(plan);
        if(!lines[0].EndsWith("state_index") || !lines.Last().StartsWith("grid-close,") || new FileInfo(plan+".states.bin").Length!=2L*SmoothedHsv.Dimensions*8 || lines.Where(l=>l.Contains(",match,")).Any(l=>l.Split(',').Length!=12))throw new Exception("Binary training plan or closing reference changed");
    }
}
static class SmoothedHsvChecks
{
    public static void Run(string root) {
        string folder=Path.Combine(root,"build","smooth-hsv-test-config");Directory.CreateDirectory(folder);
        ManagedCalibration.CheckHsvGrid(Path.Combine(folder,"grid"));
        var scenes=new[]{new CalibrationScene {area=0,moments=new double[14]},new CalibrationScene {area=1,moments=[1,1,1,1,1,1,1,1,1,1,1,1,1,1]},new CalibrationScene {area=1,moments=[1,0,0,1,0,0,1,1,1,1,1,1,1,1]}};
        foreach(var scene in scenes){scene.display_width=256;scene.display_height=144;}
        var states=scenes.Select(s=>s.SmoothState(1000)).ToArray();
        if(states.Any(s=>Math.Abs(s.Sum()-1)>1e-10 || s.Any(v=>v<0)))throw new Exception("Histogram lost population");
        var model=CalibrationModel.FromSmoothHistogram(states,[0,Math.Log(1.5),Math.Log(2)],1000);
        var duplicate=CalibrationModel.FromSmoothHistogram([states[0],states[1],states[1],states[2]],[0,Math.Log(1.5),Math.Log(1.5),Math.Log(2)],1000);
        if(duplicate.Centers.Length!=3 || Math.Abs(duplicate.Predict(states[1])-model.Predict(states[1]))>1e-12)throw new Exception("Repeated anchor changed prediction");
        string path=Path.Combine(folder,"runtime-model.json");File.WriteAllText(path,model.Json("test"));
        var loaded=CalibrationModel.Load(path);if(!loaded.SmoothHistogram || Math.Abs(loaded.Predict(states[1])-model.Predict(states[1]))>1e-12)throw new Exception("Histogram model roundtrip changed prediction");
        // Version-12 populations migrate without changing their measured gains.
        static double[] Column(int n,int source,double width,bool circular) {
            var values=Enumerable.Range(0,n).Select(dest=> {int delta=Math.Abs(dest-source);return Math.Exp(-(circular?Math.Min(delta,n-delta):delta)/width);}).ToArray();
            double sum=values.Sum();return values.Select(v=>v/sum).ToArray();
        }
        static double[] Population(double[] widths,bool reduced=false) {
            int ns=reduced?6:12,nv=reduced?16:64;
            var h=Column(12,0,widths[0],true);var sat=Column(ns,ns-1,widths[1],false);
            double coordinate=reduced?20.0*15/63:20;int lo=(int)coordinate;double f=coordinate-lo;
            var va=Column(nv,lo,widths[2],false);var vb=Column(nv,Math.Min(lo+1,nv-1),widths[2],false);
            return Enumerable.Range(0,12*ns*nv).Select(i=>h[i/(ns*nv)]*sat[(i/nv)%ns]*(va[i%nv]*(1-f)+vb[i%nv]*f)).ToArray();
        }
        double[] oldWidths=[1,6,9];var oldState=Population(oldWidths);var expected=Population(SmoothedHsv.Smoothing,true);
        string legacy=Path.Combine(folder,"legacy-model.json");
        File.WriteAllText(legacy,System.Text.Json.JsonSerializer.Serialize(new {version=12,interpolation="laplace-hsv-gaussian-spike",features=9216,smoothing=oldWidths,peak_content_nits=1000,centers=new[]{oldState},coefficients=new[]{Math.Log(1.5)}}));
        var migrated=CalibrationModel.Load(legacy);
        if(SmoothedHsv.DistanceSquared(migrated.Centers[0],expected)>1e-20 || Math.Abs(migrated.Coefficients[0]-Math.Log(1.5))>1e-12)throw new Exception("Legacy histogram migration changed measurements or smoothing");
        if(Math.Abs(SmoothedHsv.LogWeight(.005)+1)>1e-12 || SmoothedHsv.LogWeight(0)!=0)throw new Exception("Cubic exponential weight changed");
        File.WriteAllText(legacy,System.Text.Json.JsonSerializer.Serialize(new {version=13,interpolation="laplace-hsv-cubic",features=9216,smoothing=new double[]{1.2,6,14},bandwidth=.005,weight_power=3,peak_content_nits=1000,centers=new[]{Population(new double[]{1.2,6,14})},coefficients=new[]{Math.Log(1.5)}}));
        var previous=CalibrationModel.Load(legacy);if(SmoothedHsv.DistanceSquared(previous.Centers[0],expected)>1e-20 || previous.Coefficients[0]!=migrated.Coefficients[0])throw new Exception("Version-13 migration failed");
        Backend.WriteModel(legacy,Path.Combine(folder,"migrated.bin"));
        using(var input=new BinaryReader(File.OpenRead(Path.Combine(folder,"migrated.bin"))))if(input.ReadUInt32()!=0x454c5041u)throw new Exception("Legacy export retained old GPU behavior");
        Backend.WriteModel(path,Path.Combine(folder,"runtime.bin"));
        // GPU fixture consists of exact per-pixel scRGB values, matching these states.
        using(var output=new BinaryWriter(File.Create(Path.Combine(folder,"scenes.bin")))) {
            output.Write(scenes.Length);output.Write(256);output.Write(144);
            foreach(var scene in scenes) {
                output.Write(Math.Exp(model.Predict(scene)));
                foreach(double value in scene.SmoothState(1000))output.Write(value);
                for(int y=0;y<144;y++)for(int x=0;x<256;x++) {
                    bool probe=MathF.Abs((x+.5f)/256-.5f)<=.05f && MathF.Abs((y+.5f)/144-.5f)<=.05f;
                    var rgb=Enumerable.Range(0,3).Select(j=>(probe?.4:scene.area==0?0:scene.moments[j])*250/80).ToArray();
                    double[] converted=[1.660491*rgb[0]-.587641*rgb[1]-.072850*rgb[2],-.124550*rgb[0]+1.132900*rgb[1]-.008349*rgb[2],-.018151*rgb[0]-.100579*rgb[1]+1.118730*rgb[2]];
                    foreach(double value in converted)output.Write((float)value);output.Write(1f);
                }
            }
        }
        Console.WriteLine("Smoothed HSV: grid coverage, V=0 anchors, population, duplicate weighting and serialization passed.");
    }
}
