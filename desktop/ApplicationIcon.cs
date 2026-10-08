// SPDX-License-Identifier: GPL-3.0-only
namespace OledCalibration;

static class ApplicationIcon
{
    public static Icon Monitor { get; } = Load();

    static Icon Load()
    {
        using var stream = typeof(ApplicationIcon).Assembly.GetManifestResourceStream("OledCalibration.MonitorIcon")
            ?? throw new InvalidOperationException("The application icon is missing.");
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }
}
