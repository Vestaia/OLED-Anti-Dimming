// SPDX-License-Identifier: GPL-3.0-only
#pragma once
#include <algorithm>
#include <array>
#include <vector>
#include <fstream>
#include <cmath>
#include <stdexcept>
#include <memory>
#include <emmintrin.h>
#include "cluster_distance.hpp"
struct OnlineTraining {
    struct Point { std::array<double,49> state{}; double gain=0; unsigned measurements=1; double selfSimilarity=0; std::shared_ptr<std::vector<double>> histogram; };
    std::vector<Point> points, checkpoint;
    bool enabled=false;
    unsigned dimensions=14;
    bool histogram=false,cubicHistogram=false;
    bool clusters=false,scaled=false,kernel=false,kernelCoverage=false,cube=false;
    void load(const std::string& path) {
        points.clear(); checkpoint.clear(); enabled=false;cubicHistogram=false;histogram=clusters=scaled=kernel=kernelCoverage=cube=false;dimensions=14;
        std::ifstream input(path,std::ios::binary);
        if(!input)return;
        unsigned magic=0,count=0;
        input.read((char*)&magic,4);input.read((char*)&count,4);
        if((magic!=0x314e4c4f&&magic!=0x324e4c4f&&magic!=0x334e4c4f&&magic!=0x344e4c4f&&magic!=0x354e4c4f&&magic!=0x364e4c4f&&magic!=0x374e4c4f&&magic!=0x384e4c4f&&magic!=0x394e4c4f)||(count==0&&magic!=0x374e4c4f&&magic!=0x384e4c4f&&magic!=0x394e4c4f)||count>8192)throw std::runtime_error("Invalid online training seed");
        cubicHistogram=magic==0x384e4c4f||magic==0x394e4c4f;histogram=cubicHistogram||magic==0x374e4c4f;cube=magic==0x364e4c4f;kernelCoverage=magic==0x344e4c4f;kernel=kernelCoverage||magic==0x354e4c4f;scaled=cube||kernel||magic==0x334e4c4f;clusters=scaled||magic==0x324e4c4f;dimensions=histogram?(magic==0x394e4c4f?1152:9216):scaled?49:clusters?40:14;
        for(unsigned i=0;i<count;i++) {
            Point point;
            if(histogram)point.histogram=std::make_shared<std::vector<double>>(dimensions);
            input.read((char*)(histogram?point.histogram->data():point.state.data()),dimensions*sizeof(double));input.read((char*)&point.gain,sizeof(double));
            if(!input||!std::isfinite(point.gain))throw std::runtime_error("Truncated online training seed");
            if(histogram)for(double value:*point.histogram)if(!std::isfinite(value)||value<0)throw std::runtime_error("Invalid histogram coordinates");
            for(double value:point.state)if(!std::isfinite(value))throw std::runtime_error("Invalid online coordinates");
            if(kernel)point.selfSimilarity=gaussianClusterSimilarity(point.state,point.state);
            points.push_back(point);
        }
        enabled=true;
    }
    static double histogramDistance(const std::vector<double>& a,const std::vector<double>& b) {
        __m128d sum=_mm_setzero_pd();for(unsigned i=0;i<a.size();i+=2){auto delta=_mm_sub_pd(_mm_loadu_pd(a.data()+i),_mm_loadu_pd(b.data()+i));sum=_mm_add_pd(sum,_mm_mul_pd(delta,delta));}double values[2];_mm_storeu_pd(values,sum);return std::sqrt((values[0]+values[1])*(a.size()==1152?.125:1));
    }
    double predict(const std::vector<double>& state) const {
        if(!histogram){std::array<double,49> old{};std::copy_n(state.begin(),dimensions,old.begin());return predict(old);}
        double maximum=-INFINITY,total=0,sum=0;
        for(const auto& point:points) {
            double d=histogramDistance(state,*point.histogram),a=-d*d/(2*.004*.004),b=std::log(10.)-d/.00088,m=(std::max)(a,b),lw=cubicHistogram?-std::pow(d/.005,3):m+std::log(std::exp(a-m)+std::exp(b-m));
            if(lw>maximum){double scale=std::exp(maximum-lw);sum*=scale;total*=scale;maximum=lw;}
            double w=std::exp(lw-maximum);sum+=w*point.gain;total+=w;
        }
        return total>0?(std::max)(0.,sum/total):0;
    }
    void observe(const std::vector<double>& state,double signal) {
        if(!histogram){std::array<double,49> old{};std::copy_n(state.begin(),dimensions,old.begin());observe(old,signal);return;}
        if(!enabled||signal<=0||!std::isfinite(signal))return;
        double gain=(std::max)(0.,std::log(signal/250));
        for(auto& point:points)if(histogramDistance(state,*point.histogram)<1e-12){point.gain=(point.gain*point.measurements+gain)/(point.measurements+1);point.measurements++;return;}
        if(points.size()>=8192)throw std::runtime_error("Histogram training sample limit reached");
        Point point;point.histogram=std::make_shared<std::vector<double>>(state);point.gain=gain;points.push_back(point);
    }
    double predict(const std::array<double,49>& state) const {
        double numerator=0,denominator=0;
        double querySelf=kernel?gaussianClusterSimilarity(state,state):0;
        for(const auto& point:points) {
            double distance=.0001;
            if(cube)distance=std::cbrt(cubeClusterCost(state,point.state))+.0001;else if(scaled)distance=std::sqrt((std::max)(0.,kernel?gaussianClusterDistanceWithSelf(state,point.state,querySelf,point.selfSimilarity,kernelCoverage):scaledClusterDistance(state,point.state)))+.0001;else if(clusters){std::array<double,40> a{},b{};std::copy_n(state.begin(),40,a.begin());std::copy_n(point.state.begin(),40,b.begin());distance+=clusterDistance(a,b);}else for(unsigned i=0;i<dimensions;i++){double delta=state[i]-point.state[i];distance+=delta*delta;}
            double weight=scaled?1/std::pow(distance,7):1/(distance*distance*distance);
            numerator+=weight*point.gain;denominator+=weight;
        }
        return denominator>0?(std::max)(0.,numerator/denominator):0;
    }
    void observe(const std::array<double,49>& state,double signal) {
        if(!enabled||signal<=0||!std::isfinite(signal))return;
        Point value{state,(std::max)(0.,std::log(signal/250))};
        if(kernel)value.selfSimilarity=gaussianClusterSimilarity(state,state);
        for(auto& point:points){double distance=0;for(unsigned i=0;i<dimensions;i++){double delta=point.state[i]-state[i];distance+=delta*delta;}if(distance<1e-16){if(scaled){point.gain=(point.gain*point.measurements+value.gain)/(point.measurements+1);point.measurements++;}else point=value;return;}}
        if(points.size()>=2048)throw std::runtime_error("Online training sample limit reached");
        points.push_back(value);
    }
    void beginSweep(){if(enabled)checkpoint=points;}
    void endSweep(double drift){if(enabled&&drift>.5)points=checkpoint;}
};

#ifdef OLED_TESTING
inline void onlineTrainingSelfTest() {
    OnlineTraining model;
    std::array<double,49> state{};state[0]=1;
    model.observe(state,500);
    if(!model.points.empty())throw std::runtime_error("Frozen model learned a sample");
    model.enabled=true;model.points.push_back({{},0});
    model.beginSweep();model.observe(state,500);
    if(std::abs(model.predict(state)-std::log(2.))>1e-6)
        throw std::runtime_error("Training did not update its next prediction");
    model.endSweep(.6);
    if(model.points.size()!=1||model.predict(state)!=0)
        throw std::runtime_error("Reference drift did not roll back training");
    model.beginSweep();model.observe(state,500);model.endSweep(.1);
    model.beginSweep();model.observe(state,750);model.endSweep(.6);
    if(model.points.size()!=2||std::abs(model.predict(state)-std::log(2.))>1e-6)
        throw std::runtime_error("Rollback did not restore an overwritten sample");
    OnlineTraining cubeModel;cubeModel.enabled=cubeModel.clusters=cubeModel.scaled=cubeModel.cube=true;cubeModel.dimensions=49;
    std::array<double,49> cubeWhite{};cubeWhite[3]=5;cubeWhite[4]=1;cubeModel.observe(cubeWhite,500);
    auto splitCube=cubeWhite;splitCube[4]=.2;splitCube[9]=5;splitCube[10]=.8;
    if(std::abs(cubeModel.predict(splitCube)-std::log(2.))>1e-10)throw std::runtime_error("Cube online predictor changed with equivalent cluster splitting");
    OnlineTraining scaled;scaled.enabled=scaled.clusters=scaled.scaled=true;scaled.dimensions=49;
    std::array<double,49> white{};white[3]=std::sqrt(250.)/(std::sqrt(350.)-std::sqrt(250.));white[4]=white[48]=1;
    scaled.observe(white,500);scaled.observe(white,1000);
    if(scaled.points.size()!=1 || std::abs(scaled.predict(white)-.5*(std::log(2.)+std::log(4.)))>1e-10)throw std::runtime_error("Scaled duplicate states did not share one contribution");
    OnlineTraining kernel;kernel.enabled=kernel.clusters=kernel.scaled=kernel.kernel=true;kernel.dimensions=49;
    kernel.observe(white,500);auto divided=white;divided[4]=.2;for(int j=0;j<4;j++)divided[6+j]=white[j];divided[10]=.8;
    if(std::abs(kernel.predict(divided)-std::log(2.))>1e-10)throw std::runtime_error("Kernel online predictor changed under component splitting");
    auto distant=white;distant[3]+=1;kernel.observe(distant,750);
    auto cost=gaussianClusterDistance(white,distant);double w=1/std::pow(std::sqrt(cost)+.0001,7),same=1/std::pow(.0001,7);
    if(std::abs(kernel.predict(white)-(same*std::log(2.)+w*std::log(3.))/(same+w))>1e-10)throw std::runtime_error("Cached kernel online distance differs from direct evaluation");
    scaled.beginSweep();scaled.observe(white,250);scaled.endSweep(.6);
    if(scaled.points.front().measurements!=2 || std::abs(scaled.predict(white)-.5*(std::log(2.)+std::log(4.)))>1e-10)throw std::runtime_error("Scaled duplicate averaging did not roll back");
}
#endif
