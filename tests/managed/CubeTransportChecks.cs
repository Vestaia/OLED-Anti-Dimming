// SPDX-License-Identifier: GPL-3.0-only
namespace OledCalibration;
static class CubeTransportChecks
{
    static double[] Solid(double[] rgb)=>ScaledColorClusters.Fit([CubeTransport.Map(rgb,1)]);
    static void Near(double value,double expected,double tolerance=1e-8){if(Math.Abs(value-expected)>tolerance)throw new Exception($"Cube transport: {value} != {expected}");}
    public static void Run(string root)
    {
        var w250=Solid([1,1,1]);var w350=Solid([1.4,1.4,1.4]);
        Near(Math.Cbrt(CubeTransport.Cost(w250,w350)),1);
        Near(Math.Cbrt(CubeTransport.Cost(Solid([1,0,0]),Solid([0,1,0]))),1);
        Near(Math.Cbrt(CubeTransport.Cost(w250,Solid([1,0,0]))),1);
        double[] Window(double area,double nits)=>ScaledColorClusters.Fit([CubeTransport.Map([nits/250,nits/250,nits/250],area),CubeTransport.Map([0,0,0],1-area)]);
        Near(Math.Cbrt(CubeTransport.Cost(Window(.4,250),Window(.6,250))),1);
        Near(Math.Cbrt(CubeTransport.Cost(Window(.4,25),Window(.6,25))),.99);
        Near(Math.Cbrt(CubeTransport.Cost(Window(.1,250),Window(.1,350))),Math.Cbrt(.1));
        var split=(double[])w250.Clone();int slot=Enumerable.Range(0,8).First(i=>split[i*6+4]>.9);int empty=Enumerable.Range(0,8).First(i=>i!=slot);
        for(int k=0;k<6;k++)split[empty*6+k]=split[slot*6+k];split[slot*6+4]=.2;split[empty*6+4]=.8;
        Near(CubeTransport.Cost(split,w250),0);Near(CubeTransport.Cost(split,w350),CubeTransport.Cost(w250,w350));
        // A greedy assignment would cost 101; residual reassignment gives 4.
        var costs=Enumerable.Repeat(100.0,64).ToArray();costs[0]=1;costs[1]=2;costs[8]=2;costs[9]=100;Near(CubeTransport.Solve(costs,[.5,.5,0,0,0,0,0,0],[.5,.5,0,0,0,0,0,0]),2);
        var random=new Random(208411);var cases=new List<object>();
        for(int trial=0;trial<80;trial++){
            var supply=Enumerable.Range(0,8).Select(_=>random.NextDouble()).ToArray();var demand=Enumerable.Range(0,8).Select(_=>random.NextDouble()).ToArray();double sa=supply.Sum(),sb=demand.Sum();for(int k=0;k<8;k++){supply[k]/=sa;demand[k]/=sb;}
            var pairCosts=Enumerable.Range(0,64).Select(_=>random.NextDouble()*5).ToArray();double answer=CubeTransport.Solve(pairCosts,supply,demand);cases.Add(new{costs=pairCosts,supply,demand,answer});
            var transpose=Enumerable.Range(0,64).Select(k=>pairCosts[(k%8)*8+k/8]).ToArray();Near(CubeTransport.Solve(transpose,demand,supply),answer);
        }
        Directory.CreateDirectory(Path.Combine(root,"build","cube-transport"));File.WriteAllText(Path.Combine(root,"build","cube-transport","solver-cases.json"),System.Text.Json.JsonSerializer.Serialize(cases));
        double[][] palette={ [.4,.4,.4],[4,4,4],[.1,.1,.1],[4,0,0],[0,4,0],[0,0,4],[4,0,4],[.2,.4,.8] };
        var states=palette.Select(Solid).ToArray();var model=CalibrationModel.FromCubeClusters(states,Enumerable.Range(0,8).Select(i=>i*.03).ToArray());
        string folder=Path.Combine(root,"build","cube-transport-test-config");Directory.CreateDirectory(folder);string json=Path.Combine(folder,"runtime-model.json");File.WriteAllText(json,model.Json(""));
        Near(CalibrationModel.Load(json).Predict(states[3]),model.Predict(states[3]));ClusterChecks.ExportLive(json,folder);
        using(var windows=new BinaryWriter(File.Create(Path.Combine(folder,"windows.bin"))))for(int i=0;i<4;i++)windows.Write((float)Math.Exp(model.Predict(Window(.4+.1*i,250))));
        Console.WriteLine("PASS: cube transport reference norms, smooth low-brightness transition, cube-root population, exact split invariance and model round trip");
    }
}
