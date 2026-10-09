// SPDX-License-Identifier: GPL-3.0-only
#pragma once
#include <array>
#include <algorithm>
#include <cmath>
#include "cube_transport.hpp"
inline double clusterDistance(const std::array<double,40>& a,const std::array<double,40>& b) {
    using Cluster=std::array<double,5>;
    auto canonical=[](const std::array<double,40>& state){std::array<Cluster,8> c{};for(int i=0;i<8;i++)for(int j=0;j<5;j++)c[i][j]=state[i*5+j];std::sort(c.begin(),c.end(),[](const Cluster& x,const Cluster& y){for(int j:{0,1,2,4,3}){if(x[j]!=y[j])return x[j]<y[j];}return false;});return c;};
    auto ca=canonical(a),cb=canonical(b);
    auto directed=[](const auto& x,const auto& y){double cost[8][8]{},left[8]{},right[8]{};for(int i=0;i<8;i++){left[i]=x[i][3];right[i]=y[i][3];for(int j=0;j<8;j++){for(int k:{0,1,2,4}){double d=x[i][k]-y[j][k];cost[i][j]+=d*d;}}}double result=0;for(int step=0;step<15;step++){double best=1e300;int ii=-1,jj=-1;for(int i=0;i<8;i++)if(left[i]>1e-12)for(int j=0;j<8;j++)if(right[j]>1e-12&&cost[i][j]<best){best=cost[i][j];ii=i;jj=j;}if(ii<0)break;double mass=(std::min)(left[ii],right[jj]);result+=mass*best;left[ii]-=mass;right[jj]-=mass;}return result;};
    return .5*(directed(ca,cb)+directed(cb,ca));
}

// Version 8: reference-normalized 4D centers, squared total transported distance,
// and an independent quadratic non-black coverage term.
inline double scaledClusterDistance(const std::array<double,49>& a,const std::array<double,49>& b) {
    using Cluster=std::array<double,6>;
    auto canonical=[](const auto& state){std::array<Cluster,8> c{};for(int i=0;i<8;i++)for(int j=0;j<6;j++)c[i][j]=state[i*6+j];std::sort(c.begin(),c.end(),[](const Cluster& x,const Cluster& y){for(int j:{0,1,2,3,5,4}){if(x[j]!=y[j])return x[j]<y[j];}return false;});return c;};
    auto ca=canonical(a),cb=canonical(b);
    auto directed=[](const auto& x,const auto& y){double cost[8][8]{},left[8]{},right[8]{};for(int i=0;i<8;i++){left[i]=x[i][4];right[i]=y[i][4];for(int j=0;j<8;j++){for(int k:{0,1,2,3,5}){double d=x[i][k]-y[j][k];cost[i][j]+=d*d;}}}double result=0;for(int step=0;step<15;step++){double best=1e300;int ii=-1,jj=-1;for(int i=0;i<8;i++)if(left[i]>1e-12)for(int j=0;j<8;j++)if(right[j]>1e-12&&cost[i][j]<best){best=cost[i][j];ii=i;jj=j;}if(ii<0)break;double mass=(std::min)(left[ii],right[jj]);result+=mass*std::sqrt(best);left[ii]-=mass;right[jj]-=mass;}return result;};
    double brightness=100/(std::sqrt(350.)-std::sqrt(250.));double areaWeight=100-brightness*brightness*.025;double area=a[48]-b[48];
    double transport=.5*(directed(ca,cb)+directed(cb,ca));return transport*transport+areaWeight*area*area;
}

// Version 9: analytic Gaussian kernel expectation over isotropic k-means components.
inline double gaussianClusterSimilarity(const std::array<double,49>& a,const std::array<double,49>& b) {
 double result=0;for(int i=0;i<8;i++)if(a[i*6+4]>0)for(int j=0;j<8;j++)if(b[j*6+4]>0){
  double q=1+(a[i*6+5]*a[i*6+5]+b[j*6+5]*b[j*6+5])/4, d=0;
  for(int axis=0;axis<4;axis++){double delta=a[i*6+axis]-b[j*6+axis];d+=delta*delta;}
  result+=a[i*6+4]*b[j*6+4]*std::exp(-d/(2*q))/(q*q);
 }return result;
}
inline double gaussianClusterDistanceWithSelf(const std::array<double,49>& a,const std::array<double,49>& b,double aa,double bb,bool legacyCoverage=false) {
 double normalization=2*(1-std::exp(-.5)),area=a[48]-b[48];
 return (std::max)(0.,aa+bb-2*gaussianClusterSimilarity(a,b))/normalization+(legacyCoverage?(100-2*(1-std::exp(-std::pow(100/(std::sqrt(350.)-std::sqrt(250.)),2)*.025/2))/normalization)*area*area:0);
}

inline double gaussianClusterDistance(const std::array<double,49>& a,const std::array<double,49>& b){return gaussianClusterDistanceWithSelf(a,b,gaussianClusterSimilarity(a,a),gaussianClusterSimilarity(b,b));}
