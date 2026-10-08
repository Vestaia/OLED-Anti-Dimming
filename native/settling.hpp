#pragma once
// Observe a noise-floor plateau; slowing down alone is not evidence of settling.
struct FrameSettling {
 std::deque<double> values;int confirmations=0;bool stable=false;double variation=0,drift=0;
 void reset(){values.clear();confirmations=0;stable=false;}
 bool add(double y,double noise){
  values.push_back(y);if(values.size()>3)values.pop_front();if(values.size()<3)return false;
  double a=std::abs(values[1]-values[0]),b=std::abs(values[2]-values[1]);variation=std::sqrt((a*a+b*b)/2);drift=std::abs(values[2]-values[0])/2;
  double floor=std::max(.08,6*noise),slopeFloor=std::max(.012,3*noise/std::sqrt(2.));
  bool noLongerFalling=b>=.7*a||std::max(a,b)<floor/3;
  stable=variation<=floor&&drift<=slopeFloor&&noLongerFalling;return stable;
 }
};
inline void settlingSelfTest(){
 FrameSettling detector;
 for(int i=0;i<15;++i)if(detector.add(80-i*.3,.005))throw std::runtime_error("Settling accepted continuing drift");
 detector.reset();int accepted=-1;
 for(int i=0;i<60;++i){double y=50+20*std::exp(-i/5.)+.006*(i%2?1:-1);if(detector.add(y,.005)){accepted=i;break;}}
 if(accepted<20||accepted>50)throw std::runtime_error("Settling failed decaying-response test");
 detector.reset();for(int i=0;i<30;++i)if(detector.add(50+(i%2?.5:-.5),.005))throw std::runtime_error("Settling accepted oscillation above noise floor");
}
