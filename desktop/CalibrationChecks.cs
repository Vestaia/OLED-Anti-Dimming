namespace OledCalibration;
static partial class ManagedCalibration
{
    public static void VerifyCalibration(string root) {GamutChecks(root);HistogramChecks.Run(root);}
}
