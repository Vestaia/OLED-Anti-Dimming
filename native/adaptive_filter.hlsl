// SPDX-License-Identifier: GPL-3.0-only
// Feature statistics are descriptive, not an additive subpixel-power model.
Texture2D<float4> Scene : register(t0);
Texture3D<float4> Profile : register(t1);
StructuredBuffer<float4> Basis : register(t2);
StructuredBuffer<float4> Model : register(t3);
StructuredBuffer<float4> Gain : register(t4);
Texture1D<float4> Vcgt : register(t5);
SamplerState Linear : register(s0);
cbuffer FilterParams : register(b1) {
 uint width,height,hdrInput,profileSize;
 uint count,hasVcgt,enabled,unused;
 float peak,baseline,epsilon,whiteNits;
 float4 scale[4];
 uint gridWidth,gridHeight,groupsX,groupsY;
 uint localNeighbors,pad0,pad1,pad2;
};
RWStructuredBuffer<float4> Partial : register(u0);
RWStructuredBuffer<float4> Result : register(u1);
static const float3x3 to2020=float3x3(.627404,.329283,.0433136,.069097,.91954,.0113612,.0163916,.0880132,.895595);
static const float3x3 to709=float3x3(1.660491,-.587641,-.072850,-.124550,1.132900,-.008349,-.018151,-.100579,1.118730);
float3 pq(float3 x){float3 p=pow(max(x,0)/10000,.1593017578125);return pow((.8359375+18.8515625*p)/(1+18.6875*p),78.84375);}
float3 unpq(float3 x){float3 p=pow(max(x,0),1/78.84375);return 10000*pow(max(p-.8359375,0)/(18.8515625-18.6875*p),1/.1593017578125);}
float3 srgb(float3 x){return lerp(12.92*x,1.055*pow(max(x,0),1/2.4)-.055,x>.0031308);}
float3 unsrgb(float3 x){return lerp(x/12.92,pow(max((x+.055)/1.055,0),2.4),x>.04045);}
float3 deviceCurve(float3 code){uint n;Vcgt.GetDimensions(n);float3 uv=(code*(n-1)+.5)/n;return float3(Vcgt.SampleLevel(Linear,uv.r,0).r,Vcgt.SampleLevel(Linear,uv.g,0).g,Vcgt.SampleLevel(Linear,uv.b,0).b);}
float3 managed(float3 raw){
 if(profileSize==0&&hasVcgt==0)return hdrInput!=0?mul(to2020,raw)*80:mul(to2020,unsrgb(raw))*whiteNits;
 float3 code=hdrInput!=0?pq(mul(to2020,raw)*80):raw;
 if(profileSize>0)code=Profile.SampleLevel(Linear,(code*(profileSize-1)+.5)/profileSize,0).rgb;
 // Device curve precedes scene analysis and the final linear brightness gain.
 if(hasVcgt!=0)code=deviceCurve(code);
 return hdrInput!=0?unpq(code):mul(to2020,unsrgb(code))*whiteNits;
}
groupshared float4 sums[256*4];
// Aspect-aware sampling grid with private group histograms.
// Integer atomics are confined to shared memory, never a global hot bin.
groupshared uint histogram[512];
groupshared float4 encoded[4];
[numthreads(16,16,1)] void HistogramFeatures(uint3 id:SV_DispatchThreadID,uint3 group:SV_GroupID,uint lane:SV_GroupIndex) {
 histogram[lane]=0;histogram[lane+256]=0;GroupMemoryBarrierWithGroupSync();
 if(id.x<gridWidth && id.y<gridHeight) {
 float3 c=managed(Scene.Load(int3(min(uint2((id.xy+.5)*float2(width,height)/float2(gridWidth,gridHeight)),uint2(width-1,height-1)),0)).rgb)/250.;
 float3 x=clamp(sqrt(max(c,0)/40.)*7.,0,7);
 uint3 lo=min(uint3(x),6);float3 t=x-lo;
 [unroll] for(uint k=0;k<8;k++) {
  uint3 d=uint3(k&1,(k>>1)&1,(k>>2)&1);
  float3 w=lerp(1-t,t,float3(d));uint3 b=lo+d;
  InterlockedAdd(histogram[b.x*64+b.y*8+b.z],uint(round(w.x*w.y*w.z*4096.)));
 }
 }
 GroupMemoryBarrierWithGroupSync();
 for(uint k=lane;k<128;k+=256) {
  uint b=k*4;
  Partial[(group.y*groupsX+group.x)*128+k]=float4(histogram[b],histogram[b+1],histogram[b+2],histogram[b+3])/(float(gridWidth)*gridHeight*4096.);
 }
}
[numthreads(128,1,1)] void HistogramInfer(uint lane:SV_GroupIndex) {
 float4 projected[4];for(uint j=0;j<4;j++)projected[j]=0;
 for(uint b=lane;b<512;b+=128) {
  float mass=0;for(uint g=0;g<groupsX*groupsY;g++)mass+=Partial[g*128+b/4][b%4];
  mass-=b==0?1.:0.;
  for(uint j=0;j<4;j++)projected[j]+=mass*Basis[b*4+j];
 }
 for(uint j=0;j<4;j++)sums[lane*4+j]=projected[j];
 GroupMemoryBarrierWithGroupSync();
 for(uint step=64;step>0;step/=2){if(lane<step)for(uint j=0;j<4;j++)sums[lane*4+j]+=sums[(lane+step)*4+j];GroupMemoryBarrierWithGroupSync();}
 if(lane==0)for(uint j=0;j<4;j++)encoded[j]=sums[j];
 GroupMemoryBarrierWithGroupSync();
 float contribution=0,normalization=0;
 for(uint n=lane;n<count;n+=128){float distance=0;[unroll]for(uint j=0;j<14;j++){float d=encoded[j/4][j%4]/scale[j/4][j%4]-Model[n*4+j/4][j%4];distance+=d*d;}float soft=distance+.0001;float weight=localNeighbors!=0?1/(soft*soft*soft):exp(-epsilon*epsilon*distance);contribution+=Model[n*4+3].z*weight;normalization+=weight;}
 sums[lane*4]=float4(contribution,normalization,0,0);GroupMemoryBarrierWithGroupSync();
 for(uint step=64;step>0;step/=2){if(lane<step)sums[lane*4]+=sums[(lane+step)*4];GroupMemoryBarrierWithGroupSync();}
 if(lane==0)Result[0]=float4(enabled!=0&&count>0?exp(max(localNeighbors!=0?sums[0].x/max(sums[0].y,1e-30):sums[0].x-baseline,0)):1,0,0,0);

}
struct VOut{float4 pos:SV_POSITION;float2 tex:TEXCOORD;};
float4 FilterPS(VOut input):SV_TARGET {
 // Exact pixel fetch: correction cannot blur or resample the desktop.
 float4 raw=Scene.Load(int3(uint2(input.pos.xy),0));
 if(hdrInput!=0&&profileSize==0&&hasVcgt==0)return float4(raw.rgb*Gain[0].x,raw.a);
 float3 light=managed(raw.rgb)*Gain[0].x;
 if(hdrInput!=0)return float4(mul(to709,light)/80,raw.a);
 float3 code=srgb(mul(to709,light)/whiteNits);
 // No panel peak clamp: the monitor determines its output ceiling.
 return float4(code,raw.a);
}
