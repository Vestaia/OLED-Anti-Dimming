#pragma once
struct ColorCalibration {
 struct Query {int color;double area;int other=-1;bool check=false;double span=0;};
 std::deque<Query> queue;std::map<int,std::map<double,double>> curves;std::set<std::pair<int,int>> scheduled;
 std::ofstream file;bool active=false,autoAfterWhite=false,checks=false,finishing=false;ULONGLONG stimulus=0,started=0;bool pending=false;
 Query current{};std::vector<double> readings;double reference=0;long exposure=0;std::string status,path;
 const char* name(int c){switch(c){case 0:return "red";case 1:return "green";case 2:return "blue";case 3:return "white";case 5:return "cyan";case 6:return "magenta";case 7:return "yellow";default:return "none";}}
 void presented(ULONGLONG now){if(pending){stimulus=now;pending=false;}}
 void schedule(int c,double a,bool check=false,double span=0){int k=int(std::round(a*10000));if(scheduled.insert({c,k}).second)queue.push_back({c,k/10000.,-1,check,span});}
 void show(Query q){current=q;params.mode=q.other<0?5:6;params.color=q.color;params.pad=float(std::max(0,q.other));params.area=float(q.area);params.nits=100;params.probeNits=100;stimulus=GetTickCount64();pending=true;readings.clear();status="Color load: "+std::string(name(q.color))+" / "+name(q.other)+" area "+std::to_string(int(q.area*100))+"%";white.status=status;}
 void stop(const std::string& s){active=false;autoAfterWhite=false;file.close();log(s);white.status=s;params.mode=4;params.area=.01f;params.nits=params.probeNits=100;}
 void begin(){if(!roiReady||!camera.fixedExposure()||!camera.fixedWhiteBalance()){stop("Color sweep needs center ROI and fixed camera controls");return;}white.stage=WhiteCalibration::Idle;autoAfterWhite=false;active=true;checks=false;finishing=false;started=GetTickCount64();exposure=camera.exp;curves.clear();scheduled.clear();queue.clear();readings.clear();if(file.is_open())file.close();file.clear();path="color-observations-"+std::to_string(started)+".csv";file.open(path);file<<"host_ms,camera_sample_s,color,other,area,surround_signal_nits,probe_signal_nits,exposure_log2_s,camera_code,standard_error,n_frames,kind\n";
  for(int c:{3,0,1,2,5,6,7})for(double a:{.01,.1,.2,.4,.6,.8,1.})schedule(c,a);next();log("Color sweeps: fixed white 1% center at 100 signal nits; only surrounds change; "+path);
 }
 double interpolate(int c,double a){auto& k=curves[c];auto hi=k.lower_bound(a);if(hi==k.begin())return hi->second;if(hi==k.end())return k.rbegin()->second;auto lo=std::prev(hi);return lo->second+(hi->second-lo->second)*(a-lo->first)/(hi->first-lo->first);}
 void next(){
  if(queue.empty()&&!checks){checks=true;for(int c:{3,0,1,2,5,6,7}){auto copy=curves[c];for(auto i=copy.begin(),j=std::next(i);j!=copy.end();++i,++j)schedule(c,(i->first+j->first)/2,true,j->first-i->first);} }
  if(queue.empty()&&!finishing){finishing=true;for(auto pair:{std::pair<int,int>{0,1},{0,2},{1,2},{3,0},{3,5},{6,7}})for(double a:{.4,.8,1.})queue.push_back({pair.first,a,pair.second});queue.push_back({3,.01,-1,true});}
  if(queue.empty()){file.close();active=false;log("PASS color acquisition: "+path+"; fit model offline before trusting scalar load");white.status="Color sweeps complete; white comparison resumed";white.replay();return;}
  auto q=queue.front();queue.pop_front();show(q);
 }
 void tick(ULONGLONG now){if(autoAfterWhite&&white.valid&&!white.busy())begin();if(!active)return;if(now-started>240000){stop("Color acquisition incomplete: time limit");return;}if(!frameValid||pending||camera.arrival<stimulus+ULONGLONG(std::ceil(white.cameraDelayMs+white.settlingMs)))return;
  if(camera.exp!=exposure||clipped>.001){stop("Color acquisition invalid: exposure change or clipping");return;}readings.push_back((meanRGB[0]+meanRGB[1]+meanRGB[2])/3);if(readings.size()<3)return;double y=std::accumulate(readings.begin(),readings.end(),0.)/3,var=0;for(double r:readings)var+=(r-y)*(r-y);double se=std::sqrt(var/6);
  std::string kind=current.other>=0?"mixture_holdout":finishing?"final_reference":current.check?"adaptive_check":"anchor";
  file<<now<<','<<camera.sampleTime<<','<<name(current.color)<<','<<name(current.other)<<','<<current.area<<",100,100,"<<exposure<<','<<y<<','<<se<<",3,"<<kind<<std::endl;
  if(current.color==3&&current.area==.01){if(!finishing)reference=y;else if(std::abs(y-reference)>std::max(2.,3*se)){stop("Color acquisition rejected: reference drift");return;}}
  if(current.other<0&&!finishing){double predicted=current.check?interpolate(current.color,current.area):y;curves[current.color][current.area]=y;if(current.check&&std::abs(y-predicted)>std::max(1.,3*se)&&current.span>.025){schedule(current.color,current.area-current.span/4,true,current.span/2);schedule(current.color,current.area+current.span/4,true,current.span/2);}}
  next();
 }
} colors;

