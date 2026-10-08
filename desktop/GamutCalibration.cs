using System.Diagnostics;
using System.Text.Json;

namespace OledCalibration;

static partial class ManagedCalibration
{
    // Distances and spreads use the same sqrt-linear RGB coordinates as the
    // histogram encoder. The cube ends at the user-selected nominal peak.
    public static List<double[]> GamutPoints(double peak, int interior = 8)
    {
        var points=new List<double[]>();
        void Add(double[] p) {if(!points.Any(q=>Distance(p,q)<1e-12))points.Add(p);}
        // 8 vertices, 12 edge centers, 6 face centers (cube center follows below).
        foreach(double r in new[]{0.0,.5,1})foreach(double g in new[]{0.0,.5,1})foreach(double b in new[]{0.0,.5,1})
            if(new[]{r,g,b}.Count(v=>v==.5)<3)Add([r,g,b]);
        for(int i=1;i<8;i++)Add([i/8.0,i/8.0,i/8.0]);
        double reference=Math.Sqrt(100/peak);Add([reference,reference,reference]);
        // Deterministic greedy maximin selection, not a claim of a global optimum.
        var candidates=new List<double[]>();
        for(int r=1;r<8;r++)for(int g=1;g<8;g++)for(int b=1;b<8;b++)candidates.Add([r/8.0,g/8.0,b/8.0]);
        for(int i=0;i<interior;i++) {
            var best=candidates.MaxBy(p=>points.Min(q=>Distance(p,q)))!;
            Add(best);candidates.Remove(best);
        }
        return points;
    }
    static double Distance(double[] a,double[] b)=>a.Zip(b,(x,y)=>(x-y)*(x-y)).Sum();
    static CalibrationScene GamutPattern(string folder,string name,double[][] centers,double spread,double area,bool training,double peak,int seed,double[]? mixtureWeights=null)
    {
        Directory.CreateDirectory(folder);
        var random=new Random(seed);
        // Weighted 2?5-mode mixtures; independent colors within each cluster
        // vary from tile to tile, including mixtures of dark and bright modes.
        var weights=mixtureWeights??centers.Select(_=>.25+random.NextDouble()).ToArray();
        if(weights.Length!=centers.Length||weights.Any(v=>v<=0))throw new Exception("Invalid mixture weights");double sum=weights.Sum();
        double Gaussian()=>Math.Sqrt(-2*Math.Log(Math.Max(1e-12,random.NextDouble())))*Math.Cos(2*Math.PI*random.NextDouble());
        const int width=160,height=80,tile=8;
        const int tiles=200,columns=20;
        var counts=weights.Select(w=>(int)Math.Floor(w/sum*tiles)).ToArray();
        var order=Enumerable.Range(0,weights.Length).OrderByDescending(i=>weights[i]/sum*tiles-counts[i]).ToArray();
        for(int i=0,left=tiles-counts.Sum();i<left;i++)counts[order[i%order.Length]]++;
        var assignments=counts.SelectMany((n,i)=>Enumerable.Repeat(i,n)).ToArray();
        for(int i=assignments.Length-1;i>0;i--){int j=random.Next(i+1);(assignments[i],assignments[j])=(assignments[j],assignments[i]);}
        var palette=new double[tiles][];
        for(int i=0;i<palette.Length;i++) {
            int cluster=assignments[i];
            palette[i]=centers[cluster].Select(v=>{double x=Math.Clamp(v+spread*Gaussian(),0,1);return x*x*peak/250;}).ToArray();
        }
        var pixels=Enumerable.Range(0,width*height).Select(i=>palette[i%width/tile+i/width/tile*columns]).ToArray();
        string path=Path.GetFullPath(Path.Combine(folder,name+".bin"));Texture(path,pixels,width,height);
        return new CalibrationScene {name=name,scene=name,asset=path.Replace('\\','/'),area=area,training=training,moments=Moments(pixels),gamut_points=centers,gamut_spread=spread,gamut_weights=weights.Select(v=>v/sum).ToArray()};
    }
    public static CalibrationMetadata GamutSeeds(string folder,string monitor,double peak,bool express=false, int displayWidth=2560, int displayHeight=1440, bool mosaic=true)
    {
        var meta=new CalibrationMetadata {histogram_pca=true,gamut_sampling_version=1,pattern_version=3,monitor_device=monitor,peak_content_nits=peak,express_mode=express,mosaic_probe=mosaic,display_width=displayWidth,display_height=displayHeight};
        var points=GamutPoints(peak,express?4:8);
        for(int i=0;i<points.Count;i++) {
            string family="gamut-solid-"+i;
            var source=GamutPattern(folder,family,[points[i]],0,1,true,peak,2084+i);
            foreach(double area in points[i].All(v=>v==0)?new[]{1.0}:express?new[]{1.0,.4}:new[]{1.0,.6,.25}) {
                meta.scenes.Add(CopyGamut(source,family+"_"+Number(area),area,true));
            }
            if(i%4==0&&points[i].Any(v=>v>0))meta.scenes.Add(CopyGamut(source,family+"_check",.45,false));
        }
        if(!express)for(int i=0;i<12;i++) {
            int modes=2+i%4;
            // Include a bright and a dark mode; additional modes span the gamut.
            var centers=Enumerable.Range(0,modes).Select(k=>points[(i*7+k*11)%points.Count]).ToArray();
            var dark=points.Where(p=>p.Max()<=.5).ToArray();var bright=points.Where(p=>p.Max()>=.875).ToArray();
            centers[0]=dark[i%dark.Length];centers[1]=bright[(i*3)%bright.Length];
            string family="gamut-cluster-"+i;
            var source=GamutPattern(folder,family,centers,new[]{.025,.075,.15}[i%3],1,true,peak,92084+i);
            foreach(double area in new[]{1.0,.6,.25})meta.scenes.Add(CopyGamut(source,family+"_"+Number(area),area,true));
            meta.scenes.Add(CopyGamut(source,family+"_check",.45,false));
        }
        if(!express) {
            var palettes=new double[][][]{[[.95,.95,.95],[.15,.15,.15]],[[1,.2,.1],[.12,.2,.15]],[[.15,.95,.8],[.2,.1,.2]]};
            for(int i=0;i<palettes.Length;i++)foreach(double brightShare in new[]{.2,.8}) {
                string family=$"gamut-weighted-{i}-{brightShare:g}";
                var source=GamutPattern(folder,family,palettes[i],.025,1,true,peak,302084+i,[brightShare,1-brightShare]);
                foreach(double area in new[]{1.0,.6,.25})meta.scenes.Add(CopyGamut(source,family+"_"+Number(area),area,true));
                meta.scenes.Add(CopyGamut(source,family+"_check",.45,false));
            }
        }
        StampProbe(meta,meta.scenes);return meta;
    }
    static CalibrationScene CopyGamut(CalibrationScene source,string name,double area,bool training)=>new() {
        name=name,scene=source.scene,asset=source.asset,area=area,training=training,mosaic_probe=source.mosaic_probe,display_width=source.display_width,display_height=source.display_height,moments=source.moments,gamut_points=source.gamut_points,gamut_spread=source.gamut_spread,gamut_weights=source.gamut_weights
    };
    static double GamutTolerance(Dictionary<string,string> row)=>Math.Max(.5,Math.Abs(Value(row,"reference_code"))*.01);
    static CalibrationScene[] GamutErrors(List<Dictionary<string,string>> validation,CalibrationMetadata meta)
    {
        var lookup=meta.scenes.ToDictionary(r=>r.name);var closing=Closing(validation);
        var saturated=validation.Where(r=>r["role"]=="panel_plateau").Select(r=>r["name"]).ToHashSet();
        return validation.Where(r=>r["role"]=="predicted_adaptive"&&lookup.ContainsKey(r["name"])&&!saturated.Contains(r["name"])&&ClosingError(closing,lookup[r["name"]])<=.5)
            .Where(r=>Math.Abs(Value(r,"camera_code")-Value(r,"reference_code"))>GamutTolerance(r))
            .OrderByDescending(r=>Math.Abs(Value(r,"camera_code")-Value(r,"reference_code"))/GamutTolerance(r))
            .Select(r=>lookup[r["name"]]).DistinctBy(r=>r.name).ToArray();
    }
    public static List<CalibrationScene> GamutNeighbors(List<Dictionary<string,string>> validation,CalibrationMetadata meta,string folder,bool whitesOnly=false)
    {
        var result=new List<CalibrationScene>();
        var existing=meta.scenes.Select(r=>r.Histogram()).ToList();
        bool Novel(CalibrationScene r) {
            var h=r.Histogram();if(existing.Any(old=>Distance(old,h)<1e-10))return false;existing.Add(h);return true;
        }
        foreach(var source in GamutErrors(validation,meta).Where(r=>r.gamut_points.Length>0&&(!whitesOnly||r.gamut_points.All(p=>Math.Abs(p[0]-p[1])+Math.Abs(p[1]-p[2])<1e-8))).Take(4)) {
            // Bisect the neighboring measured-area intervals around the failed
            // holdout. Train at new coordinates, never overwrite earlier samples.
            var measured=meta.scenes.Where(r=>r.training&&r.asset==source.asset).Select(r=>r.area).ToArray();
            double lower=measured.Where(a=>a<source.area).DefaultIfEmpty(.01).Max();
            double upper=measured.Where(a=>a>source.area).DefaultIfEmpty(1).Min();
            string prefix="adapt_gamut_"+Path.GetFileName(folder)+"_"+result.Count;
            foreach(double area in new[]{(lower+source.area)/2,(upper+source.area)/2}) {
                var r=CopyGamut(source,prefix+"_area_"+Number(area),area,true);if(Novel(r))result.Add(r);
            }
            // Change both brightness and population spread around this region.
            var moved=source.gamut_points.Select(p=>p.Select(v=>v*.94).ToArray()).ToArray();
            var color=GamutPattern(folder,prefix+"_color",moved,source.gamut_spread*1.15,source.area,true,meta.peak_content_nits,2084+meta.scenes.Count+result.Count,source.gamut_weights.Length==0?null:source.gamut_weights);
            StampProbe(meta,[color]);
            if(Novel(color))result.Add(color);
            // Independent local holdouts (no training/validation coordinate reuse).
            var hold=CopyGamut(source,prefix+"_hold",(lower+3*source.area)/4,false);if(Novel(hold))result.Add(hold);
            var colorHold=GamutPattern(folder,prefix+"_color_hold",source.gamut_points.Select(p=>p.Select(v=>v*.97).ToArray()).ToArray(),source.gamut_spread*1.075,source.area,false,meta.peak_content_nits,4084+meta.scenes.Count+result.Count,source.gamut_weights.Length==0?null:source.gamut_weights);
            StampProbe(meta,[colorHold]);
            if(Novel(colorHold))result.Add(colorHold);
        }
        return result;
    }
    public static List<CalibrationScene> GamutProbes(CalibrationMetadata meta,string folder,int count=6)
    {
        // Generate candidates on demand; no camera labels are required to rank
        // under-covered regions. Use both gamut coverage and distribution novelty.
        var random=new Random(2084+meta.scenes.Count);
        var covered=meta.measured_gamut_states.Count>0?meta.scenes.Where(r=>meta.measured_gamut_states.Contains(r.name)).ToArray():meta.scenes.Where(r=>r.training).ToArray();
        var knownPoints=covered.SelectMany(r=>r.gamut_points).ToList();
        var knownHistograms=covered.Select(r=>r.Histogram()).ToList();
        var encoder=meta.histogram_basis;
        double[] Encode(double[] h)=>encoder==null?h:encoder.Project(h);
        var knownStates=knownHistograms.Select(Encode).ToList();
        var candidates=new List<(double[][] points,double spread,double area,double[] state)>();
        for(int i=0;i<128;i++) {
            int modes=i%2==0?1:2+i%4;
            var points=Enumerable.Range(0,modes).Select(_=>Enumerable.Range(0,3).Select(_=>random.NextDouble()).ToArray()).ToArray();
            double spread=modes==1?0:.02+random.NextDouble()*.13;
            double area=.15+random.NextDouble()*.85;
            var h=new double[512];HistogramPca.Add(h,[0,0,0],1-area);HistogramPca.Add(h,[.4,.4,.4],.01);
            foreach(var p in points)HistogramPca.Add(h,p.Select(v=>v*v*meta.peak_content_nits/250).ToArray(),(area-.01)/modes);
            candidates.Add((points,spread,area,Encode(h)));
        }
        var result=new List<CalibrationScene>();
        for(int i=0;i<count&&candidates.Count>0;i++) {
            // Alternate point-space and distribution-space exploration rather
            // than letting either distance metric dominate every selection.
            double Score((double[][] points,double spread,double area,double[] state) c)=>i%2==0
                ? c.points.Average(p=>knownPoints.Select(q=>Distance(p,q)).DefaultIfEmpty(1).Min())
                : knownStates.Select(q=>Distance(c.state,q)).DefaultIfEmpty(1).Min();
            var best=candidates.MaxBy(Score);candidates.Remove(best);
            var probe=GamutPattern(folder,"gamut-probe-"+Path.GetFileName(Path.GetDirectoryName(folder))+"-"+Path.GetFileName(folder)+"-"+meta.scenes.Count+"-"+i,best.points,best.spread,best.area,false,meta.peak_content_nits,82084+meta.scenes.Count+i);
            StampProbe(meta,[probe]);
            if(meta.scenes.Concat(result).Any(r=>Distance(r.Histogram(),probe.Histogram())<1e-10))continue;
            result.Add(probe);knownPoints.AddRange(best.points);knownStates.Add(Encode(probe.Histogram()));
        }
        return result;
    }
    static void StampProbe(CalibrationMetadata meta,IEnumerable<CalibrationScene> scenes) {
        foreach(var scene in scenes){scene.mosaic_probe=meta.mosaic_probe;scene.display_width=meta.display_width;scene.display_height=meta.display_height;}
    }
    static void MarkGamutMeasured(CalibrationMetadata meta,List<Dictionary<string,string>> observations)
    {
        var lookup=meta.scenes.ToDictionary(r=>r.name);var closing=Closing(observations);
        var names=meta.measured_gamut_states.ToHashSet();
        foreach(var row in observations.Where(r=>r["role"]=="matched"||r["role"]=="predicted_adaptive"))
            if(lookup.TryGetValue(row["name"],out var scene)&&ClosingError(closing,scene)<=.5)names.Add(scene.name);
        meta.measured_gamut_states=names.Order().ToList();
    }
    static void GamutCheckpoint(string output,CalibrationMetadata meta,List<Dictionary<string,string>> rows,CalibrationModel fit,string monitor) {
        File.WriteAllText(Path.Combine(output,"checkpoint-metadata.json"),JsonSerializer.Serialize(meta));
        SaveCsv(Path.Combine(output,"checkpoint-training.csv"),rows);
        File.WriteAllText(Path.Combine(output,"checkpoint-model.json"),fit.Json(monitor));
    }
    static async Task RunGamut(string root,string output,bool fresh,string monitor,string camera,int rounds,Action<string> log,Action<Process?> track,CancellationToken token,bool whitesOnly,double peak,bool express)
    {
        CalibrationMetadata meta;List<Dictionary<string,string>> rows;CalibrationModel fit;
        string run=DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        if(fresh) {
            var screen=Screen.AllScreens.First(s=>s.DeviceName==monitor);
            meta=GamutSeeds(output,monitor,peak,express,screen.Bounds.Width,screen.Bounds.Height);

            if(!express)AddBenchmarks(root,output,meta);
            StampProbe(meta,meta.scenes);
            meta.histogram_basis=HistogramPca.Fit(meta.scenes.Select(r=>r.Histogram()),peak);
            log($"Gamut coarse pass: {meta.scenes.Count(r=>r.training)} states; vertices, edges, faces, gray axis, maximin interior and clustered mixtures");
            acquisitionPhase="Initial coarse calibration";
            Plan(Path.Combine(output,"initial-plan.csv"),meta.scenes.Where(r=>r.training));acquisitionStart=5;acquisitionEnd=55;
            rows=await Acquire(root,Path.Combine(output,"initial-plan.csv"),Path.Combine(output,"initial.csv"),monitor,camera,log,track,token);
            Step(55,"Initial coarse calibration");fit=Fit(rows,meta);
        } else {
            meta=JsonSerializer.Deserialize<CalibrationMetadata>(File.ReadAllText(Path.Combine(output,"metadata.json")))!;
            if(meta.monitor_device!=monitor||Math.Abs(meta.peak_content_nits-peak)>1e-6||meta.express_mode!=express)throw new Exception("Use New calibration when changing display, peak brightness or mode for gamut calibration.");
            var screen=Screen.AllScreens.First(s=>s.DeviceName==monitor);
            if(meta.mosaic_probe&&(meta.display_width!=screen.Bounds.Width||meta.display_height!=screen.Bounds.Height))throw new Exception("Display resolution changed. Create a new calibration for the mosaic probe.");
            rows=ReadCsv(Path.Combine(output,"combined-training.csv"));fit=Fit(rows,meta);
        }
        MarkGamutMeasured(meta,rows);GamutCheckpoint(output,meta,rows,fit,monitor);
        string validationFolder=Path.Combine(output,"gamut-validation-"+run);Directory.CreateDirectory(validationFolder);
        var exploratory=GamutProbes(meta,Path.Combine(validationFolder,"probes"),express?3:6);
        meta.scenes.AddRange(exploratory);log($"Probing {exploratory.Count} under-covered gamut distributions");
        var holdouts=meta.scenes.Where(r=>!r.training).ToArray();
        acquisitionPhase="Initial validation";
        Plan(Path.Combine(validationFolder,"plan.csv"),holdouts,fit);acquisitionStart=55;acquisitionEnd=70;Step(55,"Initial validation");
        var validation=await Acquire(root,Path.Combine(validationFolder,"plan.csv"),Path.Combine(validationFolder,"validation.csv"),monitor,camera,log,track,token);
        MarkGamutMeasured(meta,validation);
        bool refined=false;
        for(int round=1;round<=(express?0:rounds);round++) {
            token.ThrowIfCancellationRequested();
            string folder=Path.Combine(output,$"gamut-round-{run}-{round}");
            var unresolved=GamutErrors(validation,meta);
            var added=GamutNeighbors(validation,meta,folder,whitesOnly);
            if(!added.Any(r=>r.training)){log("No unresolved, reachable gamut regions need new samples.");break;}
            refined=true;meta.scenes.AddRange(added);
            log($"Gamut refinement {round}: {added.Count(r=>r.training)} new matches around {unresolved.Length} unresolved checks");
            acquisitionPhase=$"Refinement step {round}";
            Plan(Path.Combine(folder,"plan.csv"),added.Where(r=>r.training),fit);acquisitionStart=70+(round-1)*20/Math.Max(1,rounds);acquisitionEnd=acquisitionStart+10/Math.Max(1,rounds);
            rows.AddRange(await Acquire(root,Path.Combine(folder,"plan.csv"),Path.Combine(folder,"observations.csv"),monitor,camera,log,track,token));
            MarkGamutMeasured(meta,rows);Step(acquisitionEnd,acquisitionPhase);fit=Fit(rows,meta);GamutCheckpoint(output,meta,rows,fit,monitor);
            // Avoid spending every round on regions already inside tolerance.
            var freshProbes=GamutProbes(meta,Path.Combine(folder,"probes"),4);meta.scenes.AddRange(freshProbes);
            var local=unresolved.Concat(added.Where(r=>!r.training)).Concat(freshProbes).DistinctBy(r=>r.name).ToArray();
            Plan(Path.Combine(folder,"validation-plan.csv"),local,fit);acquisitionStart=acquisitionEnd;acquisitionEnd=70+round*20/Math.Max(1,rounds);
            validation=await Acquire(root,Path.Combine(folder,"validation-plan.csv"),Path.Combine(folder,"validation.csv"),monitor,camera,log,track,token);
            MarkGamutMeasured(meta,validation);
        }
        if(refined) {
            string folder=Path.Combine(output,"gamut-final-validation-"+run);Directory.CreateDirectory(folder);
            acquisitionPhase="Final validation";
            Plan(Path.Combine(folder,"plan.csv"),meta.scenes.Where(r=>!r.training),fit);acquisitionStart=90;acquisitionEnd=96;
            validation=await Acquire(root,Path.Combine(folder,"plan.csv"),Path.Combine(folder,"validation.csv"),monitor,camera,log,track,token);
        }
        MarkGamutMeasured(meta,validation);token.ThrowIfCancellationRequested();Step(97,"Saving");
        File.WriteAllText(Path.Combine(output,"metadata.json"),JsonSerializer.Serialize(meta,new JsonSerializerOptions{WriteIndented=true}));SaveCsv(Path.Combine(output,"combined-training.csv"),rows);Heatmap(output,meta,validation);
        string pending=Path.Combine(output,"runtime-model-pending.json");File.WriteAllText(pending,fit.Json(monitor));File.Move(pending,Path.Combine(output,"runtime-model.json"),true);
        int remaining=GamutErrors(validation,meta).Length;
        File.WriteAllText(Path.Combine(output,"gamut-status.json"),JsonSerializer.Serialize(new{remaining_reachable_checks=remaining,round_limit=rounds,matched_samples=fit.Centers.Length-1,camera_relative_tolerance=.01,camera_code_tolerance_floor=.5}));
        log(remaining==0?"Gamut checks are within camera matching tolerance.":$"{remaining} reachable checks remain above tolerance; refinement round limit reached. Refine to add more samples.");Step(100,"Complete");
    }
}
