// SPDX-License-Identifier: GPL-3.0-only
namespace OledCalibration;
static partial class ManagedCalibration
{
    static CalibrationMetadata HsvGridSeeds(string folder,string monitor,double peak,bool express,int width,int height,bool hasWhiteSubpixel=false)
    {
        var meta=new CalibrationMetadata {has_white_subpixel=hasWhiteSubpixel,smoothed_hsv=true,gamut_sampling_version=2,pattern_version=4,monitor_device=monitor,peak_content_nits=peak,express_mode=express,mosaic_probe=true,display_width=width,display_height=height};
        Directory.CreateDirectory(folder);
        double[] areas=express?[1,.75,.5,.25,.1]:[1,.8,.6,.4,.2,.1,.05];
        int brightnessSteps=express?8:10,hueSteps=express?6:9;
        double[] saturations=express?[1,.5]:[1,.75,.5,.25];
        double[] Color(double hue,double saturation,double brightness) {
            double h=hue/60,chroma=brightness*saturation,x=chroma*(1-Math.Abs(h%2-1)),m=brightness-chroma;
            double[] rgb=h<1?[chroma,x,0]:h<2?[x,chroma,0]:h<3?[0,chroma,x]:h<4?[0,x,chroma]:h<5?[x,0,chroma]:[chroma,0,x];
            return rgb.Select(v=>Math.Sqrt((v+m)/peak)).ToArray();
        }
        void Solid(double hue,double saturation,double brightness) {
            string family=$"hsv-{hue:g}-{saturation:g}-{brightness:g}";
            var point=Color(hue,saturation,brightness);
            string path=Path.GetFullPath(Path.Combine(folder,family+".bin"));
            var rgb=point.Select(v=>v*v*peak/250).ToArray();Texture(path,[rgb],1,1);
            foreach(double area in areas)meta.scenes.Add(new CalibrationScene {name=family+"_"+Number(area),scene=family,asset=path.Replace('\\','/'),area=area,training=true,moments=Moments([rgb]),gamut_points=[point],grid_hue=hue,grid_saturation=saturation,grid_brightness=brightness});
        }
        for(int k=brightnessSteps;k>=1;k--) {
            double brightness=peak*Math.Pow(k/(double)brightnessSteps,2);
            Solid(0,0,brightness);
            foreach(double saturation in saturations)for(int h=0;h<hueSteps;h++)Solid(h*360.0/hueSteps,saturation,brightness);
        }
        Solid(0,0,0);foreach(double saturation in saturations)for(int h=0;h<hueSteps;h++)Solid(h*360.0/hueSteps,saturation,0);
        StampProbe(meta,meta.scenes);return meta;
    }
}
