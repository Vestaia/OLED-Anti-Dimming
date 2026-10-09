// SPDX-License-Identifier: GPL-3.0-only
namespace OledCalibration;

static class TestProgram
{
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            ApplicationConfiguration.Initialize();
            if(args is ["--ui-checks"]){QualityAndTrayChecks.Run();return 0;}
            if(args is ["--profile-histogram",var source,var output]){HistogramProfile.Run(source,output);return 0;}
            if(args is ["--smooth-hsv-checks"]){SmoothedHsvChecks.Run(Backend.Root);return 0;}
            if(args is ["--export-cluster-fixtures",var model,var folder]) { ClusterChecks.ExportLive(model,folder);return 0; }
            if(args is ["--cube-checks"]){CubeTransportChecks.Run(Backend.Root);return 0;}
            if(args is ["--kernel-checks"]){GaussianKernelChecks.Run(Backend.Root);return 0;}
            if(args is ["--scaled-checks"]){ScaledClusterChecks.Run(Backend.Root);return 0;}
            ManagedCalibration.VerifyCalibration(Backend.Root);
            QualityAndTrayChecks.Run();
            Console.WriteLine("PASS: gamut sampling, histogram PCA, model and report regressions");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
