#pragma once
#include "match.hpp"
#include "timing.hpp"
#include <map>
#include <deque>
#include <numeric>
#include <set>

// Included after generator/camera declarations. Small experimental state machine.
struct WhiteCalibration {
 enum Stage { Idle,LocateDark,LocateBright,Exposure,Reference,Point,FinalReference,Replay,Sync } stage=Idle;
 struct Query {double area,guess;bool checkOnly;double spacing=.02;};
 std::deque<Query> queue;std::map<double,double> knots;ObservationMatcher matcher;
 std::vector<double> readings;std::vector<unsigned char> dark;
 std::ofstream data,curve;ULONGLONG started=0,stimulus=0,replayStart=0;long exposure=0;
 double baseline=100,target=0,noise=0,tolerance=1,currentArea=.01,matchTolerance=1;int exposureAttempts=0,measurements=0;std::string resultPath;
 bool checkOnly=false,valid=false,checksScheduled=false;std::string status="White calibration: T start, V replay";
 bool timingOnly=false;bool fineStarted=false,reverseStarted=false;double fineSpacing=.02;std::set<int> fineAreas;
 void enqueueFine(double a,double spacing){int key=int(std::round(a*10000));if(key<100||key>10000||!fineAreas.insert(key).second)return;queue.push_back({key/10000.,interpolate(key/10000.),true,spacing});}
 bool pendingPresent=false;double cameraDelayMs=250,settlingMs=50,cameraFramePeriodMs=33.333;int syncIndex=0;std::vector<std::pair<double,bool>> transitions;std::vector<std::pair<double,double>> syncFrames;
 void presented(ULONGLONG now){if(!pendingPresent)return;pendingPresent=false;stimulus=now;if(stage==Sync)transitions.push_back({double(now),params.nits>50});}
 bool busy()const{return stage!=Idle&&stage!=Replay;}
 void set(double area,double signal) {params.mode=4;params.color=3;params.area=float(area);params.nits=float(signal);params.probeNits=float(signal);currentArea=area;stimulus=GetTickCount64();pendingPresent=true;readings.clear();}
 void stop(const std::string& reason) {log(reason);status=reason;stage=Idle;valid=false;if(data.is_open())data.close();if(curve.is_open())curve.close();set(.01,baseline);}
 void begin(bool locate=false) {
  if(!camera.fixedExposure()||!camera.fixedWhiteBalance()){stop("Calibration requires verified fixed exposure and white balance");return;}
  if(!roiReady&&!locate){stop("Select a small ROI inside the center patch first");return;}
  if(data.is_open())data.close();if(curve.is_open())curve.close();data.clear();curve.clear();
  std::string id=std::to_string(GetTickCount64());data.open("white-observations-"+id+".csv");curve.open("white-curve-"+id+".csv");
  resultPath="white-result-"+id+".json";
  data<<"host_ms,camera_sample_s,frame_sequence,stage,area,signal_nits,exposure_log2_s,r,g,b,score,p99,clipped\n";
  curve<<"area,corrected_signal_nits,camera_target,camera_observed,tolerance_codes,status\n";
  knots.clear();queue.clear();fineAreas.clear();fineStarted=false;reverseStarted=false;started=GetTickCount64();valid=false;checksScheduled=false;measurements=0;exposureAttempts=0;baseline=100;camera.exposure(-5L);set(.01,locate?0:baseline);stage=locate?LocateDark:Exposure;
  status="Locating center probe / preparing exposure";log("White calibration started: target is the observed 1% 100-signal-nit reference, not measured nits");
 }
 double interpolate(double a)const {auto hi=knots.lower_bound(a);if(hi==knots.begin())return hi->second;if(hi==knots.end())return knots.rbegin()->second;auto lo=std::prev(hi);double t=(a-lo->first)/(hi->first-lo->first);return std::exp(std::log(lo->second)*(1-t)+std::log(hi->second)*t);}
 void next() {
  if(queue.empty()&&!fineStarted){fineStarted=true;for(int i=1;i<50;++i)enqueueFine(.01+i*.02,.02);enqueueFine(1.,.02);log("Fine calibration: full 1%-100% range, 2 percentage-point steps; local refinement to 0.5 points when needed");}
  if(queue.empty()&&fineStarted&&!reverseStarted){reverseStarted=true;for(int i=9;i>=0;--i){double a=.05+i*.1;queue.push_back({a,interpolate(a),true});}log("Fine calibration: independent reverse-direction checks");}
  if(queue.empty()){stage=FinalReference;set(.01,baseline);status="Checking reference drift";return;}
  Query q=queue.front();queue.pop_front();checkOnly=q.checkOnly;fineSpacing=q.spacing;matcher={};matcher.trial=fineStarted?interpolate(q.area):q.guess;stage=Point;set(q.area,matcher.trial);status=(reverseStarted?"Reverse validation: ":fineStarted?"Fine calibration: ":"Coarse calibration: ")+std::to_string(int(q.area*100))+"% area";
 }
 void record(double y,const char* tag) {curve<<currentArea<<','<<matcher.trial<<','<<target<<','<<y<<','<<matchTolerance<<','<<tag<<std::endl;log(std::string(tag)+" area="+std::to_string(currentArea)+" signal="+std::to_string(matcher.trial)+" camera="+std::to_string(y)+" target="+std::to_string(target));}
 bool locate() {
  int w=camera.bmi.bmiHeader.biWidth,h=std::abs(camera.bmi.bmiHeader.biHeight),stride=(w*3+3)&~3;
  if(dark.size()!=camera.bytes.size())return false;
  // Locate the changed patch, not the brightest object in the room.
  std::vector<double> difference(w*h);double maxDiff=0;
  for(int y=0;y<h;++y)for(int x=0;x<w;++x){int yy=camera.bmi.bmiHeader.biHeight>0?h-1-y:y;int i=yy*stride+x*3;double d=0;for(int c=0;c<3;++c)d+=double(camera.bytes[i+c])-dark[i+c];d/=3;difference[y*w+x]=d;maxDiff=std::max(maxDiff,d);}
  if(maxDiff<12)return false;
  std::vector<unsigned char> seen(w*h);std::vector<int> largest;
  for(int i=0;i<w*h;++i){if(seen[i]||difference[i]<maxDiff*.55)continue;std::vector<int> component{i};seen[i]=1;
   for(size_t j=0;j<component.size();++j){int p=component[j],x=p%w,y=p/w;for(int n:{x>0?p-1:-1,x<w-1?p+1:-1,y>0?p-w:-1,y<h-1?p+w:-1})if(n>=0&&!seen[n]&&difference[n]>=maxDiff*.55){seen[n]=1;component.push_back(n);}}
   if(component.size()>largest.size())largest=std::move(component);
  }
  if(largest.size()<25)return false;double cx=0,cy=0;for(int p:largest){cx+=p%w;cy+=p/w;}cx/=largest.size();cy/=largest.size();int x=int(cx),y=int(cy);
  // Grow a broad rectangle inside the detected changed patch rather than sampling a few pixels.
  int radius=0;for(int r=2;r<std::min(w,h)/2;++r){bool inside=true;for(int dy=-r;dy<=r&&inside;++dy)for(int dx=-r;dx<=r;++dx){int xx=x+dx,yy=y+dy;if(xx<0||xx>=w||yy<0||yy>=h||difference[yy*w+xx]<maxDiff*.25){inside=false;break;}}if(!inside)break;radius=r;}
  if(radius<8)return false;radius=std::max(8,int(radius*.8));
  roi={LONG((x-radius)*640/w),LONG((y-radius)*360/h),LONG((x+radius+1)*640/w),LONG((y+radius+1)*360/h)};roiReady=true;
  log("Spatial averaging over approximately "+std::to_string((2*radius+1)*(2*radius+1))+" camera pixels inside probe");
  log("Located changed center patch ROI="+std::to_string(roi.left)+","+std::to_string(roi.top)+","+std::to_string(roi.right)+","+std::to_string(roi.bottom));return true;
 }
 void tick(ULONGLONG now) {
  if(stage==Idle)return;
  if(stage==Replay){double seconds=double(now-replayStart)/1000;int pass=int(seconds/8);double t=std::fmod(seconds,8.)/8.;double smooth=t*t*(3-2*t);double a=.01+.99*smooth;bool corrected=pass%2==1;params.area=float(a);params.nits=params.probeNits=float(corrected?interpolate(a):baseline);status=corrected?"CALIBRATED sweep 1% -> 100%":"UNCALIBRATED sweep 1% -> 100%";return;}
  // A persistent-session command may initialize timestamps after the loop's
  // cached `now`. Never subtract a future timestamp from an unsigned clock.
  if(now>=started&&now-started>540000){stop("Calibration incomplete: time ceiling reached");return;}
  if(!pendingPresent&&now>=stimulus&&now-stimulus>12000&&readings.empty()&&stage!=LocateDark&&stage!=LocateBright){stop("Calibration stopped: no fresh usable camera samples");return;}
  if(!frameValid&&stage!=LocateDark&&stage!=LocateBright)return;
  if(stage==Sync){double score=(meanRGB[0]+meanRGB[1]+meanRGB[2])/3;syncFrames.push_back({double(camera.arrival),score});
   static const bool bits[]{false,true,false,true,true,false,false,true,false,true,false,true};
   if(!pendingPresent&&now-stimulus>=400&&syncIndex<11){++syncIndex;set(.01,bits[syncIndex]?100:20);return;}
   if(syncIndex==11&&now-stimulus>=1000){try{auto d=estimateDelay(transitions,syncFrames);cameraDelayMs=d.milliseconds;auto response=estimateResponse(transitions,syncFrames,cameraDelayMs);settlingMs=response.settlingMs;cameraFramePeriodMs=response.framePeriodMs;std::ofstream timing(resultPath+".timing.json");timing<<"{\"effective_delay_ms\":"<<cameraDelayMs<<",\"observed_onset_median_ms\":"<<response.onsetMs<<",\"observed_stable_p90_ms\":"<<response.stableMs<<",\"received_frame_period_ms\":"<<response.framePeriodMs<<",\"settling_after_delay_ms\":"<<settlingMs<<",\"estimated_single_frame_measurement_ms\":"<<cameraDelayMs+settlingMs+response.framePeriodMs<<",\"stable_edges\":"<<response.transitions<<"}";log("Response stabilization p90="+std::to_string(response.stableMs)+" ms; frame interval="+std::to_string(response.framePeriodMs)+" ms; settling after delay="+std::to_string(settlingMs)+" ms");log("Timing pattern delay="+std::to_string(cameraDelayMs)+" ms contrast="+std::to_string(d.contrast)+" camera codes; adaptive settle >=50 ms; measurement=1 fresh frame");}catch(const std::exception& e){stop(e.what());return;}stage=Reference;set(.01,baseline);status="Synchronized reference: single settled frame";}return;
  }
  ULONGLONG settling=(stage==LocateDark||stage==LocateBright||stage==Exposure)?500:ULONGLONG(std::ceil(cameraDelayMs+settlingMs));
  if(pendingPresent||camera.arrival<stimulus+settling)return;
  if(stage==LocateDark){dark=camera.bytes;set(.01,baseline);stage=LocateBright;return;}
  if(stage==LocateBright){if(!locate()){stop("Could not locate center patch: position camera or select ROI manually and press T");return;}set(.01,baseline);stage=Exposure;return;}
  double score=(meanRGB[0]+meanRGB[1]+meanRGB[2])/3;
  data<<now<<','<<camera.sampleTime<<','<<camera.sequence<<','<<int(stage)<<','<<currentArea<<','<<params.nits<<','<<camera.exp<<','<<meanRGB[0]<<','<<meanRGB[1]<<','<<meanRGB[2]<<','<<score<<','<<p99<<','<<clipped<<'\n';
  if(stage!=Exposure&&(camera.exp!=exposure||!camera.fixedExposure())){stop("Exposure changed during sweep; reference invalid");return;}
  if(stage!=Exposure&&clipped>.001){stop("Camera clipping invalidates matching; shorten exposure before next sweep");return;}
  // One fresh frame after the measured response guard; no averaging wait.
  double y=score,se=0;
  if(stage==Exposure){long e=camera.exp;if(y<25)e+=camera.estep;if(y>90||p99>150||clipped>.001)e-=camera.estep;
   e=std::min(e,-5L);
   if(camera.exposureApi&&e!=camera.exp&&e>=camera.emin&&e<=camera.emax&&exposureAttempts++<10){camera.exposure(e);set(.01,baseline);return;}
   if(y<15||p99>170||clipped>.001){stop("No usable reference exposure: adjust camera framing or lighting");return;}
   exposure=camera.exp;stage=Sync;syncIndex=0;transitions.clear();syncFrames.clear();set(.01,20);status="Measuring camera delay with binary brightness pattern";return;
  }
  if(stage==Reference){target=y;noise=se;if(timingOnly){timingOnly=false;stage=Idle;log("Timing/ROI preparation complete");return;}tolerance=std::max(1.,3*se);knots[.01]=baseline;
   for(double a:{.1,.2,.4,.6,.8,1.})queue.push_back({a,baseline,false});next();return;
  }
  if(stage==FinalReference){if(std::abs(y-target)>std::max(2.,3*std::sqrt(noise*noise+se*se))){stop("Reference drift detected: saved observations are not a valid calibration");return;}
   valid=true;stage=Idle;status="White calibration ready: V corrected sweep, U uncorrected";
   std::ofstream result(resultPath);result<<"{\n  \"status\": \"camera_match_verified\",\n  \"baseline_signal_nits\": "<<baseline<<",\n  \"camera_target_code\": "<<target<<",\n  \"final_reference_code\": "<<y<<",\n  \"exposure_log2_seconds\": "<<exposure<<",\n  \"white_balance_kelvin\": "<<camera.wb<<",\n  \"elapsed_seconds\": "<<double(now-started)/1000<<",\n  \"interpolation\": \"linear_in_log_signal\",\n  \"knots\": [";bool comma=false;for(auto k:knots){if(comma)result<<',';result<<"\n    {\"area\": "<<k.first<<", \"signal_nits\": "<<k.second<<"}";comma=true;}result<<"\n  ]\n}\n";
   data.close();curve.close();log("PASS white calibration: "+std::to_string(knots.size())+" knots; camera target="+std::to_string(target)+". Alternating comparison starts automatically.");replay();return;
  }
  double allowed=std::max(tolerance,3*std::sqrt(noise*noise+se*se));matchTolerance=allowed;auto result=matcher.update(y,target,allowed);++measurements;
  if(reverseStarted&&result!=ObservationMatcher::Matched){record(y,"reverse_validation_failed");stop("Fine curve failed reverse-direction validation; inspect drift or panel dynamics");return;}
  if(result==ObservationMatcher::More){checkOnly=false;set(currentArea,matcher.trial);return;}
  if(result!=ObservationMatcher::Matched){record(y,"unreachable_or_nonconvergent");stop("Cannot match reference at this area; lower target or inspect response");return;}
  record(y,reverseStarted?"reverse_verified":fineStarted?(checkOnly?"fine_verified":"fine_adjusted"):checkOnly?"interpolation_verified":"matched");
  double a=currentArea;
  if(fineStarted){if(!reverseStarted){if(knots.size()>=160){stop("Fine calibration incomplete: refinement limit reached");return;}knots[a]=matcher.trial;if(matcher.iterations>0&&fineSpacing>.0051){enqueueFine(a-fineSpacing/2,fineSpacing/2);enqueueFine(a+fineSpacing/2,fineSpacing/2);}}next();return;}
  if(!checkOnly){if(knots.size()>=24){stop("Calibration incomplete: refinement limit reached");return;}knots[a]=matcher.trial;
   if(knots.size()>=7&&!checksScheduled&&queue.empty()){checksScheduled=true;for(auto i=knots.begin(),j=std::next(i);j!=knots.end();++i,++j){double m=(i->first+j->first)/2;queue.push_back({m,interpolate(m),true});}}
   else if(a!=.1&&a!=.2&&a!=.4&&a!=.6&&a!=.8&&a!=1.&&knots.size()<24){auto i=knots.find(a);if(i!=knots.begin()){auto lo=std::prev(i);if(a-lo->first>.04){double m=(a+lo->first)/2;queue.push_back({m,interpolate(m),true});}}auto hi=std::next(i);if(hi!=knots.end()&&hi->first-a>.04){double m=(a+hi->first)/2;queue.push_back({m,interpolate(m),true});}}
  }
  next();
 }
 void replay(){if(!valid||knots.empty()){log("No valid white calibration yet");return;}stage=Replay;params.mode=4;replayStart=GetTickCount64();status="Corrected white area sweep: U uncorrected, T recalibrate";}
} white;


