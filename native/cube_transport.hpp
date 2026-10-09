// SPDX-License-Identifier: GPL-3.0-only
#pragma once
#include <array>
#include <algorithm>
#include <cmath>
#include <stdexcept>
inline double cubeClusterCost(const std::array<double,49>& a,const std::array<double,49>& b) {
 std::array<double,64> cost{},flow{};std::array<double,8> left{},right{};std::array<double,18> potential{};
 double sumA=0,sumB=0;for(int i=0;i<8;i++){sumA+=a[i*6+4];sumB+=b[i*6+4];}
 for(int i=0;i<8;i++){left[i]=a[i*6+4]/sumA;right[i]=b[i*6+4]/sumB;for(int j=0;j<8;j++){
 double va=a[i*6+5]*a[i*6+5]/4,vb=b[j*6+5]*b[j*6+5]/4,q=1+va+vb,d=0;
 for(int axis=0;axis<4;axis++){double v=a[i*6+axis]-b[j*6+axis];d+=v*v;}
 cost[i*8+j]=(std::max)(0.,1/std::pow(1+2*va,2)+1/std::pow(1+2*vb,2)-2*std::exp(-d/(2*q))/(q*q))/0.3999993226471825;
 }}

 for(int step=0;step<15;step++){int best=-1;double value=1e30;for(int k=0;k<64;k++)if(left[k/8]>1e-12&&right[k%8]>1e-12&&cost[k]<value){best=k;value=cost[k];}if(best<0)break;double mass=(std::min)(left[best/8],right[best%8]);flow[best]+=mass;left[best/8]-=mass;right[best%8]-=mass;}
 for(int cycle=0;cycle<128;cycle++){std::array<double,16> distance{};std::array<int,16> previous;previous.fill(-1);int changed=-1;
 for(int pass=0;pass<16;pass++){changed=-1;for(int k=0;k<64;k++){int i=k/8,j=8+k%8;if(distance[i]+cost[k]<distance[j]-1e-12){distance[j]=distance[i]+cost[k];previous[j]=i;changed=j;}if(flow[k]>1e-12&&distance[j]-cost[k]<distance[i]-1e-12){distance[i]=distance[j]-cost[k];previous[i]=j;changed=i;}}if(changed<0)break;}
 if(changed<0){double answer=0;for(int k=0;k<64;k++)answer+=flow[k]*cost[k];return (std::max)(0.,answer);}
 int node=changed;for(int k=0;k<16;k++)node=previous[node];int start=node;double amount=1;
 do{int parent=previous[node];if(parent>=8)amount=(std::min)(amount,flow[node*8+parent-8]);node=parent;}while(node!=start);
 node=start;do{int parent=previous[node];if(parent<8)flow[parent*8+node-8]+=amount;else flow[node*8+parent-8]-=amount;node=parent;}while(node!=start);
 }throw std::runtime_error("Cube transport failed to converge");
}
