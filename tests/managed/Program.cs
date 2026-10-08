// SPDX-License-Identifier: GPL-3.0-only
namespace OledCalibration;

static class TestProgram
{
    [STAThread]
    static int Main()
    {
        try
        {
            ApplicationConfiguration.Initialize();
            ManagedCalibration.VerifyCalibration(Backend.Root);
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
