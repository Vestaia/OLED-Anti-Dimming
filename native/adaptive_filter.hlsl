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
 uint localNeighbors,histogramBins,pad1,pad2;
};
RWStructuredBuffer<float4> Partial : register(u0);
RWStructuredBuffer<float4> Result : register(u1);
// New histogram path uses additional slots; legacy models retain their shaders.
StructuredBuffer<float4> SmoothInput : register(t6);
RWStructuredBuffer<uint4> RawHsv : register(u2);
RWStructuredBuffer<float4> SmoothOutput : register(u3);

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
// Integer population accumulation. Achromatic mass is kept separately until
// the hue pass, avoiding twelve identical atomic updates per neutral pixel.
[numthreads(128,1,1)] void SmoothHsvSamples(uint3 id:SV_DispatchThreadID) {
 uint cells=gridWidth*gridHeight;if(id.x>=cells)return;
 uint2 p=uint2(id.x%gridWidth,id.x/gridWidth);
 float3 c=max(managed(Scene.Load(int3(min(uint2((p+.5)*float2(width,height)/float2(gridWidth,gridHeight)),uint2(width-1,height-1)),0)).rgb),0);
 float value=max(c.r,max(c.g,c.b)),delta=value-min(c.r,min(c.g,c.b));
 float saturation=value>0?delta/value:0;
 float hue=delta>0?frac((value==c.r?(c.g-c.b)/delta:value==c.g?2+(c.b-c.r)/delta:4+(c.r-c.g)/delta)/6+1):0;
 if(hue>=.999999)hue=0;
 uint ns=unused==9?6:12,nv=unused==9?16:64,svBins=ns*nv;
 float3 coordinate=float3(hue*12,saturation*saturation*(ns-1),sqrt(saturate(value/peak))*(nv-1));
 uint3 lo=min(uint3(coordinate),uint3(11,ns-1,nv-1));float3 f=saturate(coordinate-float3(lo));float chromatic=saturation*saturation;
 uint resolution=min(65536u,4000000000u/cells);
 [unroll]for(uint ds=0;ds<2;ds++)[unroll]for(uint dv=0;dv<2;dv++) {
  uint sv=min(lo.y+ds,ns-1)*nv+min(lo.z+dv,nv-1);
  float w=(ds?f.y:1-f.y)*(dv?f.z:1-f.z)*resolution;
  InterlockedAdd(RawHsv[sv].y,uint(round(w*(1-chromatic))));
  InterlockedAdd(RawHsv[lo.x*svBins+sv].x,uint(round(w*chromatic*(1-f.x))));
  InterlockedAdd(RawHsv[((lo.x+1)%12)*svBins+sv].x,uint(round(w*chromatic*f.x)));
 }
}
[numthreads(128,1,1)] void SmoothHsvConvolve(uint3 id:SV_DispatchThreadID) {
 uint ns=unused==9?6:12,nv=unused==9?16:64,svBins=ns*nv;
 uint index=id.x;if(index>=12*svBins)return;
 uint n=pad1==0?12:pad1==1?ns:nv,stride=pad1==0?svBins:pad1==1?nv:1;
 uint dest=(index/stride)%n,start=index-dest*stride,offset=pad1==0?0:pad1==1?144:144+ns*ns;
 float total=0;
 [loop]for(uint source=0;source<n;source++) {
  float sample=SmoothInput[start+source*stride].x;
  if(pad1==0)sample=float(asuint(sample))/(float(gridWidth)*gridHeight*min(65536u,4000000000u/(gridWidth*gridHeight)));
  total+=sample*Basis[offset+dest*n+source].x;
 }
 if(pad1==0)total+=float(asuint(SmoothInput[index%svBins].y))/(12.*gridWidth*gridHeight*min(65536u,4000000000u/(gridWidth*gridHeight)));
 SmoothOutput[index]=float4(total,0,0,0);
}
groupshared float smoothDistances[128];
[numthreads(128,1,1)] void SmoothHsvWeights(uint3 group:SV_GroupID,uint lane:SV_GroupIndex) {
 uint anchor=group.x,columns=unused==9?288:2304,rowSize=columns+1;float d2=0;
 [loop]for(uint j=lane;j<columns;j+=128) {
  float4 a=Model[anchor*rowSize+j],b=float4(SmoothInput[j*4].x,SmoothInput[j*4+1].x,SmoothInput[j*4+2].x,SmoothInput[j*4+3].x);
  float4 delta=a-b;d2+=dot(delta,delta);
 }
 smoothDistances[lane]=d2;GroupMemoryBarrierWithGroupSync();
 [unroll]for(uint stride=64;stride>0;stride/=2){if(lane<stride)smoothDistances[lane]+=smoothDistances[lane+stride];GroupMemoryBarrierWithGroupSync();}
 if(lane==0){float d=sqrt(max(smoothDistances[0],0)*(unused==9?.125:1)),a=-d*d/(2*.004*.004),b=log(10.)-d/.00088,m=max(a,b);float ratio=d/.005;float logWeight=unused>=8?-ratio*ratio*ratio:m+log(exp(a-m)+exp(b-m));SmoothOutput[anchor]=float4(logWeight,Model[anchor*rowSize+columns].x,0,0);}
}
[numthreads(1,1,1)] void SmoothHsvPredict() {
 float largest=-3.402823e38;[loop]for(uint i=0;i<count;i++)largest=max(largest,SmoothInput[i].x);
 float sum=0,total=0;[loop]for(uint j=0;j<count;j++){float2 pair=SmoothInput[j].xy;float w=exp(pair.x-largest);sum+=w*pair.y;total+=w;}
 Result[0]=float4(enabled!=0 && total>0?exp(max(sum/total,0)):1,0,0,0);
}
groupshared float4 sums[128*4];
// Aspect-aware grid: project local histograms before reducing their 14 PCA features.
// Integer atomics are confined to shared memory, never a global hot bin.
groupshared uint histogram[5120];
groupshared float4 encoded[4];
// Project soft HSV bins directly: a 9216-bin shared array exceeds SM5 limits.
float4 hsvFeature(float3 c, uint component) {
 c=max(c,0);float value=max(c.r,max(c.g,c.b));float delta=value-min(c.r,min(c.g,c.b));
 float hue=delta==0?0:(value==c.r?(c.g-c.b)/delta:value==c.g?2+(c.b-c.r)/delta:4+(c.r-c.g)/delta);
 hue=frac(hue/6+1);float saturation=value>0?delta/value:0;
 float3 x=float3(hue*9,saturation*saturation*7,saturate(sqrt(value/40))*127);
 uint3 lo=uint3(uint(x.x),min(uint(x.y),6),min(uint(x.z),126));float3 t=x-lo;float4 result=0;
 [unroll]for(uint k=0;k<8;k++) {
  uint3 d=uint3(k&1,(k>>1)&1,(k>>2)&1);float3 w=lerp(1-t,t,float3(d));
  uint si=lo.y+d.y;uint hi=si==0?0:(lo.x+d.x)%9;
  result+=w.x*w.y*w.z*Basis[((hi*8+si)*128+lo.z+d.z)*4+component];
 }
 return result;
}
[numthreads(16,16,1)] void HistogramFeatures(uint3 id:SV_DispatchThreadID,uint3 group:SV_GroupID,uint lane:SV_GroupIndex) {
 if(histogramBins==9216) {
  if(lane<128) {
   float4 projected[4];for(uint j=0;j<4;j++)projected[j]=0;
   for(uint sample=lane;sample<256;sample+=128) {
    uint2 p=group.xy*16+uint2(sample%16,sample/16);
    if(p.x<gridWidth&&p.y<gridHeight) {
     float3 c=managed(Scene.Load(int3(min(uint2((p+.5)*float2(width,height)/float2(gridWidth,gridHeight)),uint2(width-1,height-1)),0)).rgb)/250.;
     for(uint j=0;j<4;j++)projected[j]+=hsvFeature(c,j)/(float(gridWidth)*gridHeight);
    }
   }
   for(uint j=0;j<4;j++)sums[lane*4+j]=projected[j];
  }
 } else {
 for(uint b=lane;b<histogramBins;b+=256)histogram[b]=0;GroupMemoryBarrierWithGroupSync();
 if(id.x<gridWidth && id.y<gridHeight) {
 float3 c=managed(Scene.Load(int3(min(uint2((id.xy+.5)*float2(width,height)/float2(gridWidth,gridHeight)),uint2(width-1,height-1)),0)).rgb)/250.;
 float blockWeight=histogramBins>512?.5:1.;
 uint axis=histogramBins==5120?16:8;uint colorBins=axis*axis*axis;
 float3 x=clamp(sqrt(max(c,0)/40.)*float(axis-1),0,float(axis-1));
 uint3 lo=min(uint3(x),axis-2);float3 t=x-lo;
 [unroll] for(uint k=0;k<8;k++) {
  uint3 d=uint3(k&1,(k>>1)&1,(k>>2)&1);
  float3 w=lerp(1-t,t,float3(d));uint3 b=lo+d;
  InterlockedAdd(histogram[b.x*axis*axis+b.y*axis+b.z],uint(round(w.x*w.y*w.z*blockWeight*4096.)));
 }
 if(histogramBins>512) {
  uint brightnessBins=histogramBins-colorBins;
  float brightness=clamp(sqrt(max(dot(c,float3(.2627,.6780,.0593)),0)/40.)*float(brightnessBins-1),0,float(brightnessBins-1));
  uint low=min(uint(brightness),brightnessBins-2);float fraction=brightness-low;
  InterlockedAdd(histogram[colorBins+low],uint(round((1-fraction)*2048.)));
  InterlockedAdd(histogram[colorBins+low+1],uint(round(fraction*2048.)));
 }
 }
 GroupMemoryBarrierWithGroupSync();
 if(lane<128) {
  float4 projected[4];for(uint j=0;j<4;j++)projected[j]=0;
  for(uint b=lane;b<histogramBins;b+=128) {
   float mass=histogram[b]/(float(gridWidth)*gridHeight*4096.);
   for(uint j=0;j<4;j++)projected[j]+=mass*Basis[b*4+j];
  }
  for(uint j=0;j<4;j++)sums[lane*4+j]=projected[j];
 }
 }
 GroupMemoryBarrierWithGroupSync();
 for(uint step=64;step>0;step/=2){if(lane<step)for(uint j=0;j<4;j++)sums[lane*4+j]+=sums[(lane+step)*4+j];GroupMemoryBarrierWithGroupSync();}
 if(lane==0)for(uint j=0;j<4;j++)Partial[(group.y*groupsX+group.x)*4+j]=sums[j];

}
[numthreads(128,1,1)] void HistogramInfer(uint lane:SV_GroupIndex) {
 float4 projected[4];for(uint j=0;j<4;j++)projected[j]=0;
 for(uint g=lane;g<groupsX*groupsY;g+=128)
  for(uint j=0;j<4;j++)projected[j]+=Partial[g*4+j];
 for(uint j=0;j<4;j++)sums[lane*4+j]=projected[j];
 GroupMemoryBarrierWithGroupSync();
 for(uint step=64;step>0;step/=2){if(lane<step)for(uint j=0;j<4;j++)sums[lane*4+j]+=sums[(lane+step)*4+j];GroupMemoryBarrierWithGroupSync();}
 if(lane==0)for(uint j=0;j<4;j++)encoded[j]=sums[j]-(histogramBins==9216?Basis[j]:histogramBins>512?.5*(Basis[j]+Basis[(histogramBins==5120?4096:512)*4+j]):Basis[j]);
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

// Eight clusters, continuous circular HSV, brightness coordinate weighted 10x.
// These stages reuse the filter's two UAV slots and six SRV slots.
float3 clusterCoordinate(float3 light) {
 light=max(light,0);float value=max(light.r,max(light.g,light.b));float delta=value-min(light.r,min(light.g,light.b));float s=value>0?delta/value:0;
 float h=delta==0?0:value==light.r?(light.g-light.b)/delta:value==light.g?2+(light.b-light.r)/delta:4+(light.r-light.g)/delta;
 float angle=frac(h/6+1)*6.28318530718;return float3(s*s*cos(angle),s*s*sin(angle),10*sqrt(saturate(value/10000)));
}
groupshared float4 clusterSums[1024];
groupshared float clusterErrors[1024];
[numthreads(128,1,1)] void ClusterSamples(uint3 id:SV_DispatchThreadID) {
 uint cells=gridWidth*gridHeight;if(id.x>=cells)return;uint2 p=uint2(id.x%gridWidth,id.x/gridWidth);
 uint2 pixel=min(uint2((p+.5)*float2(width,height)/float2(gridWidth,gridHeight)),uint2(width-1,height-1));
 Partial[id.x]=float4(clusterCoordinate(managed(Scene.Load(int3(pixel,0)).rgb)),1./cells);
}
bool lexGreater(float3 a,float3 b){return a.x>b.x||(a.x==b.x&&(a.y>b.y||(a.y==b.y&&a.z>b.z)));}
[numthreads(128,1,1)] void ClusterSeedPoints(uint3 id:SV_DispatchThreadID,uint3 group:SV_GroupID,uint lane:SV_GroupIndex) {
 float4 p=id.x<gridWidth*gridHeight?Basis[id.x]:0;float best=1e30;
 if(id.x<gridWidth*gridHeight)for(uint k=0;k<pad1;k++){float3 delta=p.xyz-Gain[k*2].xyz;best=min(best,dot(delta,delta));}else best=-1;
 clusterSums[lane]=float4(p.xyz,best);GroupMemoryBarrierWithGroupSync();
 for(uint stride=64;stride>0;stride/=2){if(lane<stride){float4 a=clusterSums[lane],b=clusterSums[lane+stride];if(b.w>a.w||(b.w==a.w&&lexGreater(b.xyz,a.xyz)))clusterSums[lane]=b;}GroupMemoryBarrierWithGroupSync();}
 if(lane==0)Partial[group.x]=clusterSums[0];
}
[numthreads(128,1,1)] void ClusterSeedReduce(uint lane:SV_GroupIndex) {
 float4 selected=float4(0,0,0,-1);for(uint g=lane;g<groupsX;g+=128){float4 p=Partial[g];if(p.w>selected.w||(p.w==selected.w&&lexGreater(p.xyz,selected.xyz)))selected=p;}
 clusterSums[lane]=selected;GroupMemoryBarrierWithGroupSync();
 for(uint stride=64;stride>0;stride/=2){if(lane<stride){float4 a=clusterSums[lane],b=clusterSums[lane+stride];if(b.w>a.w||(b.w==a.w&&lexGreater(b.xyz,a.xyz)))clusterSums[lane]=b;}GroupMemoryBarrierWithGroupSync();}
 if(lane==0){Result[pad1*2]=float4(clusterSums[0].xyz,0);Result[pad1*2+1]=0;}
}
[numthreads(128,1,1)] void ClusterAssign(uint3 id:SV_DispatchThreadID,uint3 group:SV_GroupID,uint lane:SV_GroupIndex) {
 uint cells=gridWidth*gridHeight;float4 p=id.x<cells?Basis[id.x]:0;float best=1e30;uint winner=0;
 if(id.x<cells)for(uint k=0;k<8;k++){float3 d=p.xyz-Gain[k*2].xyz;float cost=dot(d,d);if(cost<best){best=cost;winner=k;}}
 for(uint k=0;k<8;k++){bool chosen=id.x<cells&&winner==k;clusterSums[k*128+lane]=chosen?float4(p.xyz*p.w,p.w):0;clusterErrors[k*128+lane]=chosen?best*p.w:0;}
 GroupMemoryBarrierWithGroupSync();
 for(uint stride=64;stride>0;stride/=2){if(lane<stride)for(uint k=0;k<8;k++){clusterSums[k*128+lane]+=clusterSums[k*128+lane+stride];clusterErrors[k*128+lane]+=clusterErrors[k*128+lane+stride];}GroupMemoryBarrierWithGroupSync();}
 if(lane<8){uint offset=(lane*groupsX+group.x)*2;Partial[offset]=clusterSums[lane*128];Partial[offset+1]=float4(clusterErrors[lane*128],0,0,0);}
}
[numthreads(128,1,1)] void ClusterReduce(uint3 group:SV_GroupID,uint lane:SV_GroupIndex) {
 float4 total=0;float error=0;for(uint g=lane;g<groupsX;g+=128){uint offset=(group.x*groupsX+g)*2;total+=Partial[offset];error+=Partial[offset+1].x;}
 clusterSums[lane]=total;clusterErrors[lane]=error;GroupMemoryBarrierWithGroupSync();
 for(uint stride=64;stride>0;stride/=2){if(lane<stride){clusterSums[lane]+=clusterSums[lane+stride];clusterErrors[lane]+=clusterErrors[lane+stride];}GroupMemoryBarrierWithGroupSync();}
 if(lane==0){float4 previous=Gain[group.x*2];float mass=clusterSums[0].w;float3 center=pad2!=0||mass<=0?previous.xyz:clusterSums[0].xyz/mass;
 Result[group.x*2]=float4(center,mass);Result[group.x*2+1]=float4(mass>0?sqrt(max(clusterErrors[0]/mass,0)):0,0,0,0);}
}
groupshared float4 queryClusters[16];
float modelValue(uint n,uint j){return Model[n*11+j/4][j%4];}
float transport(uint n) {
 float costs[64];float left[8],right[8];
 for(uint i=0;i<8;i++)for(uint j=0;j<8;j++){
  float3 center=float3(modelValue(n,j*5),modelValue(n,j*5+1),modelValue(n,j*5+2));float3 d=queryClusters[i*2].xyz-center;float r=queryClusters[i*2+1].x-modelValue(n,j*5+4);costs[i*8+j]=dot(d,d)+r*r;
 }
 float result=0;
 for(uint direction=0;direction<2;direction++) {
  for(uint i=0;i<8;i++){left[i]=direction==0?queryClusters[i*2].w:modelValue(n,i*5+3);right[i]=direction==0?modelValue(n,i*5+3):queryClusters[i*2].w;}
  for(uint step=0;step<15;step++) {
   float best=1e30;int ii=-1,jj=-1;
   for(uint i=0;i<8;i++)if(left[i]>1e-12)for(uint j=0;j<8;j++)if(right[j]>1e-12){float cost=direction==0?costs[i*8+j]:costs[j*8+i];if(cost<best){best=cost;ii=i;jj=j;}}
   if(ii<0)break;float mass=min(left[ii],right[jj]);result+=mass*best;left[ii]-=mass;right[jj]-=mass;
  }
 }
 return result*.5;
}
bool clusterLess(float4 a,float ar,float4 b,float br){return a.x<b.x||(a.x==b.x&&(a.y<b.y||(a.y==b.y&&(a.z<b.z||(a.z==b.z&&(ar<br||(ar==br&&a.w<b.w)))))));}
[numthreads(128,1,1)] void ClusterInfer(uint lane:SV_GroupIndex) {
 if(lane<16)queryClusters[lane]=Gain[lane];GroupMemoryBarrierWithGroupSync();
 if(lane==0){float total=0;for(uint k=0;k<8;k++)total+=queryClusters[k*2].w;for(uint k=0;k<8;k++)queryClusters[k*2].w/=max(total,1e-30);
 for(uint i=1;i<8;i++){float4 a=queryClusters[i*2],r=queryClusters[i*2+1];uint j=i;while(j>0&&clusterLess(a,r.x,queryClusters[(j-1)*2],queryClusters[(j-1)*2+1].x)){queryClusters[j*2]=queryClusters[(j-1)*2];queryClusters[j*2+1]=queryClusters[(j-1)*2+1];j--;}queryClusters[j*2]=a;queryClusters[j*2+1]=r;}}
 GroupMemoryBarrierWithGroupSync();float weighted=0,total=0;
 for(uint n=lane;n<count;n+=128){float soft=transport(n)+.0001;float weight=1/(soft*soft*soft);weighted+=modelValue(n,40)*weight;total+=weight;}
 clusterSums[lane]=float4(weighted,total,0,0);GroupMemoryBarrierWithGroupSync();
 for(uint stride=64;stride>0;stride/=2){if(lane<stride)clusterSums[lane]+=clusterSums[lane+stride];GroupMemoryBarrierWithGroupSync();}
 if(lane==0)Result[0]=float4(enabled!=0&&count>0?exp(max(clusterSums[0].x/max(clusterSums[0].y,1e-30),0)):1,0,0,0);
}

// Version 8 reference geometry. Each pixel/cluster occupies two float4 rows:
// four center coordinates, then mass, spread, and exact active-pixel coverage.
static const float scaledBrightness = 34.5196752347116;
static const float scaledAreaWeight = 70.209800542251;
float4 scaledCoordinate(float3 light) {
 light=max(light,0);float v=max(light.r,max(light.g,light.b));float delta=v-min(light.r,min(light.g,light.b));float sat=v>0?delta/v:0;
 float h=delta==0?0:v==light.r?(light.g-light.b)/delta:v==light.g?2+(light.b-light.r)/delta:4+(light.r-light.g)/delta;
 float angle=frac(h/6+1)*6.28318530718;float radial=sat*sat;
 float4 result=float4(radial*cos(angle)*.57735026919,radial*sin(angle)*.57735026919,radial*.81649658093,scaledBrightness*sqrt(saturate(v/10000)));
 if(unused==6){v=min(v,10000);result.xyz*=0.6680465971304523;result.w=0.2306075157459721*sqrt(v)+1.5091184299117797*(1-exp(-v/5));}
 return result;
}
groupshared float4 scaledAux[1024];
[numthreads(128,1,1)] void ScaledSamples(uint3 id:SV_DispatchThreadID) {
 uint cells=gridWidth*gridHeight;if(id.x>=cells)return;uint2 p=uint2(id.x%gridWidth,id.x/gridWidth);
 uint2 pixel=min(uint2((p+.5)*float2(width,height)/float2(gridWidth,gridHeight)),uint2(width-1,height-1));
 float3 light=max(managed(Scene.Load(int3(pixel,0)).rgb),0);Partial[id.x*2]=scaledCoordinate(light);
 Partial[id.x*2+1]=float4(1./cells,0,unused==6?0:(max(light.r,max(light.g,light.b))>.01?1./cells:0),0);
}
bool lexGreater4(float4 a,float4 b){return a.x>b.x||(a.x==b.x&&(a.y>b.y||(a.y==b.y&&(a.z>b.z||(a.z==b.z&&a.w>b.w)))));}
[numthreads(128,1,1)] void ScaledSeedPoints(uint3 id:SV_DispatchThreadID,uint3 group:SV_GroupID,uint lane:SV_GroupIndex) {
 float4 p=id.x<gridWidth*gridHeight?Basis[id.x*2]:0;float best=1e30;
 if(id.x<gridWidth*gridHeight)for(uint k=0;k<pad1;k++){float4 delta=p-Gain[k*2];best=min(best,dot(delta,delta));}else best=-1;
 clusterSums[lane]=p;clusterErrors[lane]=best;GroupMemoryBarrierWithGroupSync();
 for(uint stride=64;stride>0;stride/=2){if(lane<stride){float4 a=clusterSums[lane],b=clusterSums[lane+stride];float da=clusterErrors[lane],db=clusterErrors[lane+stride];if(db>da||(db==da&&lexGreater4(b,a))){clusterSums[lane]=b;clusterErrors[lane]=db;}}GroupMemoryBarrierWithGroupSync();}
 if(lane==0){Partial[group.x*2]=clusterSums[0];Partial[group.x*2+1]=float4(clusterErrors[0],0,0,0);}
}
[numthreads(128,1,1)] void ScaledSeedReduce(uint lane:SV_GroupIndex) {
 float4 selected=0;float best=-1;for(uint g=lane;g<groupsX;g+=128){float4 p=Partial[g*2];float d=Partial[g*2+1].x;if(d>best||(d==best&&lexGreater4(p,selected))){selected=p;best=d;}}
 clusterSums[lane]=selected;clusterErrors[lane]=best;GroupMemoryBarrierWithGroupSync();
 for(uint stride=64;stride>0;stride/=2){if(lane<stride){float4 a=clusterSums[lane],b=clusterSums[lane+stride];float da=clusterErrors[lane],db=clusterErrors[lane+stride];if(db>da||(db==da&&lexGreater4(b,a))){clusterSums[lane]=b;clusterErrors[lane]=db;}}GroupMemoryBarrierWithGroupSync();}
 if(lane==0){Result[pad1*2]=clusterSums[0];Result[pad1*2+1]=0;}
}
[numthreads(128,1,1)] void ScaledAssign(uint3 id:SV_DispatchThreadID,uint3 group:SV_GroupID,uint lane:SV_GroupIndex) {
 uint cells=gridWidth*gridHeight;float4 p=id.x<cells?Basis[id.x*2]:0;float4 aux=id.x<cells?Basis[id.x*2+1]:0;float best=1e30;uint winner=0;
 if(id.x<cells)for(uint k=0;k<8;k++){float4 delta=p-Gain[k*2];float cost=dot(delta,delta);if(cost<best){best=cost;winner=k;}}
 for(uint k=0;k<8;k++){bool chosen=id.x<cells&&winner==k;clusterSums[k*128+lane]=chosen?p*aux.x:0;scaledAux[k*128+lane]=chosen?float4(aux.x,best*aux.x,aux.z,0):0;}
 GroupMemoryBarrierWithGroupSync();
 for(uint stride=64;stride>0;stride/=2){if(lane<stride)for(uint k=0;k<8;k++){clusterSums[k*128+lane]+=clusterSums[k*128+lane+stride];scaledAux[k*128+lane]+=scaledAux[k*128+lane+stride];}GroupMemoryBarrierWithGroupSync();}
 if(lane<8){uint offset=(lane*groupsX+group.x)*2;Partial[offset]=clusterSums[lane*128];Partial[offset+1]=scaledAux[lane*128];}
}
[numthreads(128,1,1)] void ScaledReduce(uint3 group:SV_GroupID,uint lane:SV_GroupIndex) {
 float4 total=0,aux=0;for(uint g=lane;g<groupsX;g+=128){uint offset=(group.x*groupsX+g)*2;total+=Partial[offset];aux+=Partial[offset+1];}
 clusterSums[lane]=total;scaledAux[lane]=aux;GroupMemoryBarrierWithGroupSync();
 for(uint stride=64;stride>0;stride/=2){if(lane<stride){clusterSums[lane]+=clusterSums[lane+stride];scaledAux[lane]+=scaledAux[lane+stride];}GroupMemoryBarrierWithGroupSync();}
 if(lane==0){float mass=scaledAux[0].x;Result[group.x*2]=pad2!=0||mass<=0?Gain[group.x*2]:clusterSums[0]/mass;
 Result[group.x*2+1]=float4(mass,mass>0?sqrt(max(scaledAux[0].y/mass,0)):0,scaledAux[0].z,0);}
}
// Explicit component selection keeps packed six-value clusters consistent
// across the FXC compilation path used by the runtime.
float scaledModel(uint n,uint j){float4 row=Model[n*13+j/4];uint component=j%4;return component==0?row.x:component==1?row.y:component==2?row.z:row.w;}
float scaledTransport(uint n,float active) {
 float costs[64],left[8],right[8];
 for(uint i=0;i<8;i++)for(uint j=0;j<8;j++){
  float4 center=float4(scaledModel(n,j*6),scaledModel(n,j*6+1),scaledModel(n,j*6+2),scaledModel(n,j*6+3));
  float4 delta=queryClusters[i*2]-center;float r=queryClusters[i*2+1].y-scaledModel(n,j*6+5);costs[i*8+j]=dot(delta,delta)+r*r;
 }
 float result=0;
 for(uint direction=0;direction<2;direction++) {
  for(uint i=0;i<8;i++){left[i]=direction==0?queryClusters[i*2+1].x:scaledModel(n,i*6+4);right[i]=direction==0?scaledModel(n,i*6+4):queryClusters[i*2+1].x;}
  for(uint step=0;step<15;step++) {
   float best=1e30;int ii=-1,jj=-1;
   for(uint i=0;i<8;i++)if(left[i]>1e-12)for(uint j=0;j<8;j++)if(right[j]>1e-12){float cost=direction==0?costs[i*8+j]:costs[j*8+i];if(cost<best){best=cost;ii=i;jj=j;}}
   if(ii<0)break;float mass=min(left[ii],right[jj]);result+=mass*sqrt(best);left[ii]-=mass;right[jj]-=mass;
  }
 }
 float area=active-scaledModel(n,48);float transported=result*.5;return transported*transported+scaledAreaWeight*area*area;
}
float gaussianPair(float4 a,float ar,float4 b,float br) {
 float q=1+(ar*ar+br*br)*.25;float4 delta=a-b;
 return exp(-dot(delta,delta)/(2*q))/(q*q);
}
groupshared float kernelQuerySelf;
float gaussianTransport(uint n,float active) {
 float cross=0;
 for(uint i=0;i<8;i++)if(queryClusters[i*2+1].x>0)
 for(uint j=0;j<8;j++)if(scaledModel(n,j*6+4)>0){
  float4 center=float4(scaledModel(n,j*6),scaledModel(n,j*6+1),scaledModel(n,j*6+2),scaledModel(n,j*6+3));
  cross+=queryClusters[i*2+1].x*scaledModel(n,j*6+4)*gaussianPair(queryClusters[i*2],queryClusters[i*2+1].y,center,scaledModel(n,j*6+5));
 }
 float normalization=2*(1-exp(-.5));float area=active-scaledModel(n,48);
 return max(0,kernelQuerySelf+scaledModel(n,50)-2*cross)/normalization+(unused==4?(100-2*(1-exp(-scaledBrightness*scaledBrightness*.025/2))/normalization)*area*area:0);
}
// Exact component transport using residual reverse edges. Root is applied once
// to the aggregate, never separately to component population.
float cubeTransportCost(uint modelIndex) {
 float tc[64],tf[64],tl[8],tr[8];float totalLeft=0,totalRight=0;
 [loop] for(uint massIndex=0;massIndex<8;massIndex++){totalLeft+=queryClusters[massIndex*2+1].x;totalRight+=scaledModel(modelIndex,massIndex*6+4);}
 [loop] for(uint rowIndex=0;rowIndex<8;rowIndex++){
 tl[rowIndex]=queryClusters[rowIndex*2+1].x/totalLeft;tr[rowIndex]=scaledModel(modelIndex,rowIndex*6+4)/totalRight;
 [loop] for(uint colIndex=0;colIndex<8;colIndex++){
 float4 otherCenter=float4(scaledModel(modelIndex,colIndex*6),scaledModel(modelIndex,colIndex*6+1),scaledModel(modelIndex,colIndex*6+2),scaledModel(modelIndex,colIndex*6+3));
 float spreadA=queryClusters[rowIndex*2+1].y,spreadB=scaledModel(modelIndex,colIndex*6+5);float aa=1+spreadA*spreadA*.5,bb=1+spreadB*spreadB*.5;
 tc[rowIndex*8+colIndex]=max(0,1/(aa*aa)+1/(bb*bb)-2*gaussianPair(queryClusters[rowIndex*2],spreadA,otherCenter,spreadB))/0.3999993226471825;
 tf[rowIndex*8+colIndex]=0;
 }}
 [loop] for(uint greedyStep=0;greedyStep<15;greedyStep++){
 int cheapest=-1;float cheapestValue=1e20;
 [loop] for(uint candidateEdge=0;candidateEdge<64;candidateEdge++)if(tl[candidateEdge/8]>1e-7&&tr[candidateEdge%8]>1e-7&&tc[candidateEdge]<cheapestValue){cheapest=candidateEdge;cheapestValue=tc[candidateEdge];}
 if(cheapest<0)break;float transfer=min(tl[cheapest/8],tr[cheapest%8]);tf[cheapest]+=transfer;tl[cheapest/8]-=transfer;tr[cheapest%8]-=transfer;
 }
 [loop] for(uint cancelIteration=0;cancelIteration<128;cancelIteration++){
 float residualDistance[16];int residualParent[16];int lastChanged=-1;
 [loop] for(uint initializeResidual=0;initializeResidual<16;initializeResidual++){residualDistance[initializeResidual]=0;residualParent[initializeResidual]=-1;}
 [loop] for(uint relaxPass=0;relaxPass<16;relaxPass++){
 lastChanged=-1;
 [loop] for(uint residualEdge=0;residualEdge<64;residualEdge++){
 uint rowNode=residualEdge/8,colNode=8+residualEdge%8;
 if(residualDistance[rowNode]+tc[residualEdge]<residualDistance[colNode]-1e-6){residualDistance[colNode]=residualDistance[rowNode]+tc[residualEdge];residualParent[colNode]=rowNode;lastChanged=colNode;}
 if(tf[residualEdge]>1e-7&&residualDistance[colNode]-tc[residualEdge]<residualDistance[rowNode]-1e-6){residualDistance[rowNode]=residualDistance[colNode]-tc[residualEdge];residualParent[rowNode]=colNode;lastChanged=rowNode;}
 }
 if(lastChanged<0)break;
 }
 if(lastChanged<0)break;
 int cycleNode=lastChanged;
 [loop] for(uint enterCycle=0;enterCycle<16;enterCycle++){if(cycleNode<0)break;cycleNode=residualParent[cycleNode];}
 if(cycleNode<0)break;int cycleStart=cycleNode;float cycleAmount=1;
 [loop] for(uint capacityEdge=0;capacityEdge<16;capacityEdge++){
 int capParent=residualParent[cycleNode];if(capParent<0)break;
 if(capParent>=8)cycleAmount=min(cycleAmount,tf[cycleNode*8+capParent-8]);cycleNode=capParent;if(cycleNode==cycleStart)break;
 }
 cycleNode=cycleStart;
 [loop] for(uint updateEdge=0;updateEdge<16;updateEdge++){
 int flowParent=residualParent[cycleNode];if(flowParent<0)break;
 if(flowParent<8)tf[flowParent*8+cycleNode-8]+=cycleAmount;else tf[cycleNode*8+flowParent-8]-=cycleAmount;cycleNode=flowParent;if(cycleNode==cycleStart)break;
 }
 }
 float aggregate=0;[loop] for(uint sumEdge=0;sumEdge<64;sumEdge++)aggregate+=tf[sumEdge]*tc[sumEdge];return max(aggregate,0);
}
bool scaledLess(float4 a,float4 ar,float4 b,float4 br){return a.x<b.x||(a.x==b.x&&(a.y<b.y||(a.y==b.y&&(a.z<b.z||(a.z==b.z&&(a.w<b.w||(a.w==b.w&&(ar.y<br.y||(ar.y==br.y&&ar.x<br.x)))))))));}
[numthreads(128,1,1)] void ScaledInfer(uint lane:SV_GroupIndex) {
 if(lane<16)queryClusters[lane]=Gain[lane];GroupMemoryBarrierWithGroupSync();
 if(lane==0){float mass=0;for(uint k=0;k<8;k++)mass+=queryClusters[k*2+1].x;for(uint k=0;k<8;k++){queryClusters[k*2+1].x/=max(mass,1e-30);queryClusters[k*2+1].z/=max(mass,1e-30);}
 for(uint i=1;i<8;i++){float4 a=queryClusters[i*2],r=queryClusters[i*2+1];uint j=i;while(j>0&&scaledLess(a,r,queryClusters[(j-1)*2],queryClusters[(j-1)*2+1])){queryClusters[j*2]=queryClusters[(j-1)*2];queryClusters[j*2+1]=queryClusters[(j-1)*2+1];j--;}queryClusters[j*2]=a;queryClusters[j*2+1]=r;}}
 GroupMemoryBarrierWithGroupSync();float active=0;for(uint k=0;k<8;k++)active+=queryClusters[k*2+1].z;
 if(unused==4||unused==5){if(lane==0){float value=0;for(uint i=0;i<8;i++)if(queryClusters[i*2+1].x>0)for(uint j=0;j<8;j++)if(queryClusters[j*2+1].x>0)value+=queryClusters[i*2+1].x*queryClusters[j*2+1].x*gaussianPair(queryClusters[i*2],queryClusters[i*2+1].y,queryClusters[j*2],queryClusters[j*2+1].y);kernelQuerySelf=value;}GroupMemoryBarrierWithGroupSync();}
 float weighted=0,total=0;for(uint n=lane;n<count;n+=128){float r=(unused==6?pow(cubeTransportCost(n),1./3.):sqrt(max(unused>=4?gaussianTransport(n,active):scaledTransport(n,active),0)))+.0001;float r2=r*r;float weight=1/(r2*r2*r2*r);weighted+=scaledModel(n,49)*weight;total+=weight;}
 clusterSums[lane]=float4(weighted,total,0,0);GroupMemoryBarrierWithGroupSync();
 for(uint stride=64;stride>0;stride/=2){if(lane<stride)clusterSums[lane]+=clusterSums[lane+stride];GroupMemoryBarrierWithGroupSync();}
 if(lane==0)Result[0]=float4(enabled!=0&&count>0?exp(max(clusterSums[0].x/max(clusterSums[0].y,1e-30),0)):1,0,0,0);
}
