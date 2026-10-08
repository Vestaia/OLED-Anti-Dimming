namespace OledCalibration;
static class ProbePattern
{
    public static int Side(int width,int height)=>Math.Max(2,Math.Min(Math.Min(width,height)/2*2,(int)(Math.Sqrt(width*(double)height*.01)/2)*2));
    static uint Hash(uint x) {unchecked{x^=x>>16;x*=0x7feb352d;x^=x>>15;x*=0x846ca68b;x^=x>>16;return x;}}
    public static double[] Pixel(int x,int y) {
        uint key=unchecked((uint)(x/2+y*4099));
        return new[]{0u,1u,2u}.Select(c=>{double v=(Hash(unchecked(key+c*0x9e3779b9))&65535)/65535.0*2;return .4*((x&1)==0?v:2-v);}).ToArray();
    }
}
