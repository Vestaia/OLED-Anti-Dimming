// SPDX-License-Identifier: GPL-3.0-only
namespace OledCalibration;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainWindow());
    }
}
