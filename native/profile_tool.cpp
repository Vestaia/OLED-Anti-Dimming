#include <lcms2.h>
#include <fstream>
#include <iostream>
#include <vector>
#include <cmath>
#include <filesystem>
#include <stdexcept>
double decode(double x){double p=pow(x,1/78.84375);return pow(std::max(p-.8359375,0.)/(18.8515625-18.6875*p),1/.1593017578125);}
double encode(double x){double p=pow(std::max(x,0.),.1593017578125);return pow((.8359375+18.8515625*p)/(1+18.6875*p),78.84375);}
int main(int argc,char** argv){try{
 if(argc<3)throw std::runtime_error("profile-tool ICC-or-identity output-folder [hdr|sdr] [vcgt]");
 bool hdr=argc<4||std::string(argv[3])=="hdr",vcgt=argc>4&&std::string(argv[4])=="vcgt";std::filesystem::path out=argv[2];std::filesystem::create_directories(out);
 cmsHPROFILE profile=std::string(argv[1])=="identity"?nullptr:cmsOpenProfileFromFile(argv[1],"r");if(!profile&&std::string(argv[1])!="identity")throw std::runtime_error("Cannot read ICC profile");
 if(profile&&cmsGetColorSpace(profile)!=cmsSigRgbData)throw std::runtime_error("Only RGB display ICC profiles are supported");
 double inverse[9]{};cmsHPROFILE source=nullptr,destination=nullptr;cmsHTRANSFORM transform=nullptr;
 if(profile&&!hdr){source=cmsCreate_sRGBProfile();transform=cmsCreateTransform(source,TYPE_RGB_DBL,profile,TYPE_RGB_DBL,INTENT_RELATIVE_COLORIMETRIC,cmsFLAGS_NOOPTIMIZE);}
 if(profile&&hdr){
  // SDR TRCs have no absolute HDR interpretation. Use profile colorants in linear light.
  auto r=(cmsCIEXYZ*)cmsReadTag(profile,cmsSigRedColorantTag),g=(cmsCIEXYZ*)cmsReadTag(profile,cmsSigGreenColorantTag),b=(cmsCIEXYZ*)cmsReadTag(profile,cmsSigBlueColorantTag);
  if(!r||!g||!b)throw std::runtime_error("HDR requires a matrix RGB ICC profile; LUT-only profiles need an explicit PQ .cube instead");
  double m[9]{r->X,g->X,b->X,r->Y,g->Y,b->Y,r->Z,g->Z,b->Z};double det=m[0]*(m[4]*m[8]-m[5]*m[7])-m[1]*(m[3]*m[8]-m[5]*m[6])+m[2]*(m[3]*m[7]-m[4]*m[6]);if(std::abs(det)<1e-9)throw std::runtime_error("Singular ICC colorants");
  double adj[9]{m[4]*m[8]-m[5]*m[7],m[2]*m[7]-m[1]*m[8],m[1]*m[5]-m[2]*m[4],m[5]*m[6]-m[3]*m[8],m[0]*m[8]-m[2]*m[6],m[2]*m[3]-m[0]*m[5],m[3]*m[7]-m[4]*m[6],m[1]*m[6]-m[0]*m[7],m[0]*m[4]-m[1]*m[3]};for(int i=0;i<9;i++)inverse[i]=adj[i]/det;
  cmsCIExyY white{.3127,.3290,1};cmsCIExyYTRIPLE primaries{{.708,.292,1},{.170,.797,1},{.131,.046,1}};cmsToneCurve* curve=cmsBuildGamma(nullptr,1);cmsToneCurve* curves[3]{curve,curve,curve};source=cmsCreateRGBProfile(&white,&primaries,curves);cmsFreeToneCurve(curve);destination=cmsCreateXYZProfile();transform=cmsCreateTransform(source,TYPE_RGB_DBL,destination,TYPE_XYZ_DBL,INTENT_RELATIVE_COLORIMETRIC,cmsFLAGS_NOOPTIMIZE);
 }
 if(profile&&!transform)throw std::runtime_error("ICC transform could not be constructed");
 std::ofstream cube(out/"profile.cube");cube<<"TITLE \"OLED APL profile\"\nLUT_3D_SIZE 33\n";cube.precision(10);
 for(int b=0;b<33;b++)for(int g=0;g<33;g++)for(int r=0;r<33;r++){double input[3]{r/32.,g/32.,b/32.},output[3]{input[0],input[1],input[2]};if(transform){if(hdr){for(auto& v:input)v=decode(v);double xyz[3];cmsDoTransform(transform,input,xyz,1);for(int i=0;i<3;i++)output[i]=encode(inverse[i*3]*xyz[0]+inverse[i*3+1]*xyz[1]+inverse[i*3+2]*xyz[2]);}else cmsDoTransform(transform,input,output,1);}cube<<output[0]<<' '<<output[1]<<' '<<output[2]<<'\n';}
 if(vcgt){if(!profile)throw std::runtime_error("VCGT requires an ICC profile");auto curves=(cmsToneCurve**)cmsReadTag(profile,cmsSigVcgtTag);if(!curves)throw std::runtime_error("ICC contains no VCGT");std::ofstream f(out/"vcgt.bin",std::ios::binary);unsigned n=4096;f.write((char*)&n,4);for(unsigned i=0;i<n;i++){float rgba[4]{cmsEvalToneCurveFloat(curves[0],float(i)/(n-1)),cmsEvalToneCurveFloat(curves[1],float(i)/(n-1)),cmsEvalToneCurveFloat(curves[2],float(i)/(n-1)),1};f.write((char*)rgba,16);}}
 if(transform)cmsDeleteTransform(transform);if(source)cmsCloseProfile(source);if(destination)cmsCloseProfile(destination);if(profile)cmsCloseProfile(profile);
 std::cout<<(hdr?"HDR: linear ICC colorants; SDR TRCs excluded":"SDR: complete ICC transform")<<(vcgt?"; VCGT before scene analysis and correction":"; VCGT disabled")<<"\n";return 0;
 }catch(const std::exception& e){std::cerr<<e.what()<<"\n";return 1;}}
