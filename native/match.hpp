#pragma once
#include <algorithm>
#include <cmath>
#include <stdexcept>

// Bracket on observation ordering. Never convert camera ratios to light ratios.
struct ObservationMatcher {
 double low=0,high=0,trial=100;int iterations=0;double ascendingSignal=0,ascendingObserved=0,bestSignal=0,bestObserved=-1e30,previousBestSignal=0;
 enum Result { More,Matched,Unreachable,Limit,Saturated };
 Result update(double observed,double target,double tolerance) {
  if(std::abs(observed-target)<=tolerance)return Matched;
  double floor=std::max(.12,tolerance*.5);if(observed>bestObserved+floor){previousBestSignal=bestSignal;bestSignal=trial;bestObserved=observed;}if(observed<target&&high==0&&trial>ascendingSignal){if(ascendingSignal>0&&std::abs(observed-ascendingObserved)<=floor)return Saturated;ascendingSignal=trial;ascendingObserved=observed;}
  if(++iterations>20)return Limit;
  if(observed<target)low=trial;else high=trial;
  if(low>0&&high>0)trial=std::sqrt(low*high);
  else if(low>0){if(trial>=10000)return Unreachable;trial=std::min(10000.,trial*1.5);}
  else {if(trial<=.0001)return Unreachable;trial=std::max(.0001,trial/1.5);}
  return More;
 }
};
inline void matcherSelfTest() {
 for(double gamma:{.35,1.,2.2})for(double attenuation:{1.,.8,.4}) {
  ObservationMatcher s;double target=std::pow(100.,gamma);
  for(int i=0;i<30;++i){double y=std::pow(s.trial*attenuation,gamma);auto r=s.update(y,target,target*.0001);if(r==ObservationMatcher::Matched)break;if(r!=ObservationMatcher::More)throw std::runtime_error("Matcher failed nonlinear camera test");}
  if(std::abs(s.trial*attenuation-100)>1)throw std::runtime_error("Matcher assumed a camera light ratio");
 }
 ObservationMatcher capped;bool plateau=false;for(int i=0;i<20;++i){auto r=capped.update(std::min(capped.trial,640.),1000,.01);if(r==ObservationMatcher::Saturated){plateau=true;break;}if(r!=ObservationMatcher::More)throw std::runtime_error("Plateau search failed");}if(!plateau||capped.bestSignal<640||capped.bestSignal>960)throw std::runtime_error("Plateau was not inferred from observed response");
 ObservationMatcher impossible;for(int i=0;i<30;++i){auto r=impossible.update(10,100,1);if(r==ObservationMatcher::Unreachable||r==ObservationMatcher::Saturated)return;if(r!=ObservationMatcher::More)break;}
 throw std::runtime_error("Matcher failed unreachable target test");
}
