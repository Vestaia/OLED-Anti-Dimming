# SPDX-License-Identifier: GPL-3.0-only
# Original mock screenshots. Never reads the desktop or any user data.
param([string]$Destination = "$PSScriptRoot/../assets/benchmarks")
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
public static class PcaMockUi {
 public static void Render(string path,int layout,int theme) {
  using(var bitmap=new Bitmap(960,540))using(var g=Graphics.FromImage(bitmap))
  using(var text=new Font(FontFamily.GenericSansSerif,11))
  using(var heading=new Font(FontFamily.GenericSansSerif,16,FontStyle.Bold)) {
   Color[] backgrounds={Color.FromArgb(70,100,130),Color.FromArgb(18,22,30),Color.FromArgb(184,174,143),Color.FromArgb(27,58,110),Color.FromArgb(94,94,94),Color.Black};
   Color[] panels={Color.FromArgb(248,248,248),Color.FromArgb(35,39,46),Color.FromArgb(252,245,226),Color.FromArgb(222,237,255),Color.FromArgb(175,175,175),Color.White};
   Color fg=theme==1?Color.FromArgb(220,224,230):Color.FromArgb(35,40,48);
   Color accent=theme==4?Color.FromArgb(115,115,115):Color.FromArgb(35,125,185);
   Action<Color,int,int,int,int> box=(c,x,y,w,h)=>{using(var b=new SolidBrush(c))g.FillRectangle(b,x,y,w,h);};
   Action<string,int,int,bool> label=(s,x,y,bold)=>{using(var b=new SolidBrush(fg))g.DrawString(s,bold?heading:text,b,x,y);};
   Action<int,int,int,int,string> window=(x,y,w,h,title)=>{box(Color.FromArgb(70,0,0,0),x+5,y+6,w,h);box(panels[theme],x,y,w,h);box(theme==1?Color.FromArgb(55,60,68):Color.FromArgb(212,218,225),x,y,w,30);label(title,x+12,y+6,false);label("-   +   x",x+w-78,y+6,false);};
   g.Clear(backgrounds[theme]);
   box(theme==1?Color.FromArgb(25,28,34):Color.FromArgb(222,225,230),0,510,960,30);
   for(int i=0;i<5;i++)box(i==layout%5?accent:Color.Gray,18+i*48,518,26,15);
   label("12:00",882,516,false);
   string[] names={"Files","Example editor","Example page","Overview","Worksheet","Messages","Preferences","Desktop"};
   if(layout==7) {
    window(24,35,402,280,"Example document");window(456,96,454,360,"Overview");
    for(int i=0;i<10;i++){box(fg,48,95+i*15,220+(i%3)*30,2);}
    for(int i=0;i<5;i++)box(Color.FromArgb(35+i*27,110,170),482+i*72,395-i*34,45,35+i*34);
    for(int i=0;i<4;i++){box(panels[theme],33+i*86,360,44,38);label("Item "+(i+1),26+i*86,403,false);}
   } else {
    int wx=layout%3==0?22:80,wy=layout%2==0?22:50,ww=layout%3==0?916:800,wh=layout%2==0?465:425;
    window(wx,wy,ww,wh,names[layout]);
    int x=wx+18,y=wy+54,w=ww-36;
    if(layout==0) {
     box(theme==1?Color.FromArgb(45,50,58):Color.FromArgb(235,237,240),x,y,140,wh-75);
     for(int i=0;i<6;i++)label(new[]{"Home","Documents","Images","Projects","Downloads","Archive"}[i],x+10,y+16+i*35,false);
     label("Name                 Type          Modified",x+160,y,false);
     for(int i=0;i<11;i++){box(i%2==0?panels[theme]:Color.FromArgb(100,128,128,128),x+156,y+30+i*28,w-162,26);label("Sample "+(i+1)+"                 File          Example",x+170,y+35+i*28,false);}
    } else if(layout==1) {
     box(theme==1?Color.FromArgb(27,29,34):Color.FromArgb(238,238,238),x,y,130,wh-76);
     for(int i=0;i<8;i++)label("example-"+(i+1)+".txt",x+6,y+i*29,false);
     for(int i=0;i<14;i++){label((i+1).ToString(),x+148,y+i*23,false);box(i%3==0?accent:fg,x+184,y+9+i*23,120+(i*41)%290,3);}
    } else if(layout==2) {
     box(theme==1?Color.FromArgb(62,67,75):Color.White,x,y,w,28);label("example.invalid / demonstration",x+12,y+5,false);
     box(accent,x,y+48,w,102);label("Example article",x+20,y+66,true);
     for(int i=0;i<9;i++)box(fg,x+12,y+184+i*17,w-(i%4)*83,2);
     box(Color.FromArgb(80,150,120),x+w-190,y+166,172,130);
    } else if(layout==3) {
     for(int i=0;i<3;i++){box(theme==1?Color.FromArgb(53,58,67):Color.FromArgb(226,231,236),x+i*(w/3),y,w/3-10,72);label("Metric "+(i+1),x+14+i*(w/3),y+12,false);label((42+i*17).ToString(),x+14+i*(w/3),y+37,true);}
     for(int i=0;i<9;i++)box(Color.FromArgb(40+i*14,130,170),x+20+i*60,y+315-(i*37)%155,37,40+(i*37)%155);
     using(var pen=new Pen(accent,3))for(int i=0;i<10;i++)g.DrawLine(pen,x+18+i*60,y+170+(i*23)%70,x+78+i*60,y+170+((i+1)*23)%70);
    } else if(layout==4) {
     for(int r=0;r<13;r++)for(int c=0;c<8;c++){int cw=w/8;box(r==0?accent:(r%2==0?panels[theme]:Color.FromArgb(110,145,145,145)),x+c*cw,y+r*27,cw-1,26);label(r==0?"Column "+(c+1):(r*c+12).ToString(),x+c*cw+6,y+r*27+4,false);}
    } else if(layout==5) {
     box(theme==1?Color.FromArgb(46,52,60):Color.FromArgb(231,235,239),x,y,150,wh-75);
     for(int i=0;i<7;i++)label("Example group "+(i+1),x+8,y+12+i*38,false);
     for(int i=0;i<7;i++){int bx=x+170+(i%2)*190;box(i%2==0?Color.FromArgb(160,195,218):Color.FromArgb(190,190,190),bx,y+6+i*44,240,34);label("Example message "+(i+1),bx+12,y+14+i*44,false);}
    } else {
     label("Display preferences",x,y,true);
     for(int i=0;i<6;i++){label(new[]{"Appearance","Text size","Layout","Notifications","Contrast","Refresh"}[i],x+14,y+56+i*43,false);box(theme==1?Color.FromArgb(68,74,85):Color.FromArgb(218,223,229),x+w-240,y+52+i*43,190,27);box(i%2==0?accent:Color.Gray,x+w-66,y+56+i*43,20,19);}
    }
   }
   bitmap.Save(path,ImageFormat.Png);
  }
 }
}
'@
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$layouts = @('files','editor','browser','dashboard','spreadsheet','chat','settings','desktop')
$themes = @('light','dark','cream','blue','gray','contrast')
$manifest = foreach ($layout in 0..7) {
 foreach ($theme in 0..5) {
  $file = "ui-$($layouts[$layout])-$($themes[$theme]).png"
  $path = Join-Path $Destination $file
  [PcaMockUi]::Render($path,$layout,$theme)
  [ordered]@{name="ui-$($layouts[$layout])-$($themes[$theme])";file=$file;author='OLED Anti-Dimming contributors';license='GPL-3.0-only';source='Original synthetic layout rendered by tools/generate_pca_ui.ps1';sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant();privacy='Synthetic placeholders only; no screen capture or user data';usage='Unlabeled PCA training fixture'}
 }
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Destination 'ui-manifest.json') -Encoding utf8
Write-Output "Generated $($manifest.Count) original UI fixtures."
