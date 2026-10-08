using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

namespace OledCalibration;

static class StandaloneRuntime
{
    public static readonly string? Root = Prepare();
    static string? Prepare()
    {
        using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("OledCalibration.Payload");
        if(payload==null)return null;
        using var buffer=new MemoryStream();payload.CopyTo(buffer);
        string version=Convert.ToHexString(SHA256.HashData(buffer.ToArray()))[..16];
        string root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"OledCalibration","packages",version);
        Directory.CreateDirectory(root);
        buffer.Position=0;
        using var zip=new ZipArchive(buffer,ZipArchiveMode.Read);
        foreach(var entry in zip.Entries)
        {
            string path=Path.GetFullPath(Path.Combine(root,entry.FullName));
            if(!path.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("Invalid embedded runtime path");
            if(entry.Name.Length==0){Directory.CreateDirectory(path);continue;}
            if(File.Exists(path))continue;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            entry.ExtractToFile(temporary);
            try{File.Move(temporary,path,false);}catch(IOException)when(File.Exists(path)){File.Delete(temporary);}
        }
        return root;
    }
}
