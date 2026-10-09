// SPDX-License-Identifier: GPL-3.0-only
namespace OledCalibration;
// Smooth geometry, exact component transport, cube-root aggregate distance.
// No foreground/window detector. The 49th compatibility field is ignored.
static class CubeTransport
{
    public const string Id="smooth-hsv-gaussian-transport-cube-v1";
    public const double BrightnessSlope=0.2306075157459721, Offset=1.5091184299117797, Transition=5;
    public const double Normalization=0.3999993226471825, ColorScale=0.6680465971304523;
    public static ScaledColorClusters.Pixel Map(double[] rgb,double weight) {
        var p=ScaledColorClusters.Map(rgb,weight);
        double v=Math.Min(10000,Math.Max(0,rgb.Max())*250);
        double z=BrightnessSlope*Math.Sqrt(v)+Offset*(1-Math.Exp(-v/Transition));
        return new(p.X*ColorScale,p.Y*ColorScale,p.C*ColorScale,z,weight,0);
    }
    public static double Pair(double[] a,int i,double[] b,int j) {
        double va=a[i*6+5]*a[i*6+5]/4,vb=b[j*6+5]*b[j*6+5]/4,q=1+va+vb,d=0;
        for(int k=0;k<4;k++){double delta=a[i*6+k]-b[j*6+k];d+=delta*delta;}
        return Math.Max(0,1/Math.Pow(1+2*va,2)+1/Math.Pow(1+2*vb,2)-2*Math.Exp(-d/(2*q))/(q*q))/Normalization;
    }
    public static double Cost(double[] a,double[] b) {
        var costs=new double[64];for(int i=0;i<8;i++)for(int j=0;j<8;j++)costs[i*8+j]=Pair(a,i,b,j);
        return Solve(costs,Enumerable.Range(0,8).Select(i=>a[i*6+4]).ToArray(),Enumerable.Range(0,8).Select(i=>b[i*6+4]).ToArray());
    }
    // Start feasible with greedy matching, then cancel negative residual cycles.
    // Optimality is certified when no negative cycle remains.
    public static double Solve(double[] cost,double[] supply,double[] demand) {
        var left=(double[])supply.Clone();var right=(double[])demand.Clone();var flow=new double[64];
        for(int step=0;step<15;step++){
            int best=-1;double value=1e30;for(int k=0;k<64;k++)if(left[k/8]>1e-12&&right[k%8]>1e-12&&cost[k]<value){best=k;value=cost[k];}
            if(best<0)break;double mass=Math.Min(left[best/8],right[best%8]);flow[best]+=mass;left[best/8]-=mass;right[best%8]-=mass;
        }
        for(int cycle=0;cycle<128;cycle++){
            var distance=new double[16];var previous=Enumerable.Repeat(-1,16).ToArray();int changed=-1;
            for(int pass=0;pass<16;pass++){
                changed=-1;for(int k=0;k<64;k++){
                    int i=k/8,j=8+k%8;
                    if(distance[i]+cost[k]<distance[j]-1e-12){distance[j]=distance[i]+cost[k];previous[j]=i;changed=j;}
                    if(flow[k]>1e-12&&distance[j]-cost[k]<distance[i]-1e-12){distance[i]=distance[j]-cost[k];previous[i]=j;changed=i;}
                }
                if(changed<0)break;
            }
            if(changed<0)return Math.Max(0,flow.Select((mass,k)=>mass*cost[k]).Sum());
            int node=changed;for(int k=0;k<16;k++)node=previous[node];int start=node;double amount=1;
            do{int parent=previous[node];if(parent>=8)amount=Math.Min(amount,flow[node*8+parent-8]);node=parent;}while(node!=start);
            node=start;do{int parent=previous[node];if(parent<8)flow[parent*8+node-8]+=amount;else flow[node*8+parent-8]-=amount;node=parent;}while(node!=start);
        }
        throw new InvalidDataException("Transport matching did not converge");
    }
}
