#pragma once
#include <vector>
#include <utility>
#include <cmath>
#include <stdexcept>
#include <limits>
#include <algorithm>
#include <numeric>
struct ResponseTiming {double onsetMs=0,stableMs=0,framePeriodMs=0,settlingMs=50;int transitions=0;};
inline double percentile(std::vector<double> values,double fraction){if(values.empty())throw std::runtime_error("Insufficient timing samples");std::sort(values.begin(),values.end());return values[size_t(std::ceil(fraction*(values.size()-1)))];}
inline ResponseTiming estimateResponse(const std::vector<std::pair<double,bool>>& transitions,const std::vector<std::pair<double,double>>& frames,double delay){
 std::vector<double> periods,onsets,stable;
 for(size_t i=1;i<frames.size();++i){double dt=frames[i].first-frames[i-1].first;if(dt>0&&dt<200)periods.push_back(dt);}
 for(size_t i=1;i<transitions.size();++i){if(transitions[i].second==transitions[i-1].second)continue;double begin=transitions[i].first,end=i+1<transitions.size()?transitions[i+1].first:frames.back().first+1;
  std::vector<std::pair<double,double>> segment,prior;for(auto f:frames){if(f.first>=begin&&f.first<end)segment.push_back(f);if(f.first>=transitions[i-1].first&&f.first<begin)prior.push_back(f);}if(segment.size()<6||prior.size()<3)continue;
  double target=0,previous=0;for(int j=0;j<3;++j){target+=segment[segment.size()-1-j].second/3;previous+=prior[prior.size()-1-j].second/3;}double variance=0;for(int j=0;j<3;++j)variance+=std::pow(segment[segment.size()-1-j].second-target,2)/2;double band=std::max(1.,3*std::sqrt(variance));if(std::abs(target-previous)<8)continue;
  if(std::abs(segment.back().second-segment[segment.size()-3].second)>band)continue;
  for(auto f:segment)if(std::abs(f.second-previous)>band){onsets.push_back(f.first-begin);break;}
  size_t lastBad=0;bool bad=false;for(size_t j=0;j<segment.size();++j)if(std::abs(segment[j].second-target)>band){lastBad=j;bad=true;}size_t first=bad?lastBad+1:0;
  if(segment.size()-first>=3)stable.push_back(segment[first].first-begin);
 }
 if(stable.size()<4)throw std::runtime_error("Timing pattern did not establish stable plateaus; longer holds needed");
 ResponseTiming t;t.onsetMs=percentile(onsets,.5);t.stableMs=percentile(stable,.9);t.framePeriodMs=percentile(periods,.5);t.settlingMs=std::max(50.,t.stableMs-delay);t.transitions=int(stable.size());return t;
}
struct DelayEstimate {double milliseconds=0,contrast=0,error=0;};
// Fit delayed binary stimulus to camera observations. Codes are not light units.
inline DelayEstimate estimateDelay(const std::vector<std::pair<double,bool>>& transitions,const std::vector<std::pair<double,double>>& frames){
 DelayEstimate best;best.error=std::numeric_limits<double>::infinity();
 for(int lag=0;lag<=600;lag+=5){double sums[2]{},squares[2]{};int counts[2]{};
  for(auto f:frames){double t=f.first-lag;if(t<transitions.front().first)continue;bool high=false;for(auto e:transitions){if(e.first>t)break;high=e.second;}int k=high?1:0;sums[k]+=f.second;squares[k]+=f.second*f.second;++counts[k];}
  if(counts[0]<8||counts[1]<8)continue;double contrast=sums[1]/counts[1]-sums[0]/counts[0];if(contrast<8)continue;
  double error=(squares[0]-sums[0]*sums[0]/counts[0]+squares[1]-sums[1]*sums[1]/counts[1])/(counts[0]+counts[1]);
  if(error<best.error)best={double(lag),contrast,error};
 }
 if(!std::isfinite(best.error)||best.milliseconds>=600)throw std::runtime_error("Camera timing pattern could not identify a bounded delay");return best;
}
inline void timingSelfTest(){std::vector<std::pair<double,bool>> e{{0,false},{400,true},{900,false},{1200,true},{1800,false},{2400,true}};std::vector<std::pair<double,double>> f;for(int t=0;t<3000;t+=10){bool b=false;for(auto x:e){if(x.first>t-90)break;b=x.second;}f.push_back({double(t),b?83.:21.});}auto d=estimateDelay(e,f);if(std::abs(d.milliseconds-90)>5)throw std::runtime_error("Delay estimator synthetic test failed");auto r=estimateResponse(e,f,d.milliseconds);if(std::abs(r.stableMs-90)>10||r.framePeriodMs!=10||r.settlingMs<50)throw std::runtime_error("Response plateau synthetic test failed");
 std::vector<std::pair<double,bool>> slowEvents{{0,false},{900,true},{1800,false},{2700,true},{3600,false},{4500,true}};std::vector<std::pair<double,double>> slow;double y=21;for(int t=0;t<5400;t+=10){bool high=false;for(auto x:slowEvents){if(x.first>t-40)break;high=x.second;}y+=(high?83-y:21-y)*(1-std::exp(-10./90));slow.push_back({double(t),y});}auto sd=estimateDelay(slowEvents,slow);auto sr=estimateResponse(slowEvents,slow,sd.milliseconds);if(sr.stableMs<300||sr.settlingMs<=50)throw std::runtime_error("Slow panel response was mistaken for pure delay");}
