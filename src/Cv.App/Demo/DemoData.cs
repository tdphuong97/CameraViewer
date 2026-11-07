using System.Globalization;

namespace Cv.App.Demo;

/// <summary>Generic camera names and the fixed facts of the demo recorder.</summary>
internal static class DemoData
{
    public const int OfflineChannel = 5;

    /// <summary>Channel that drops its live stream for a few seconds now and then (shows the reconnect overlay).</summary>
    public const int FlakyChannel = 11;

    static readonly string[] Names =
    [
        "Cổng chính", "Sảnh tầng 1", "Hầm xe B1", "Hầm xe B2", "Thang máy 1", "Thang máy 2", "Hành lang tầng 2",
        "Hành lang tầng 3", "Kho hàng", "Bãi xe máy", "Lối thoát hiểm", "Quầy lễ tân", "Sân sau", "Phòng máy chủ",
        "Cổng phụ", "Sảnh tầng 2", "Cầu thang bộ",
    ];

    /// <summary>17 by default; CV_DEMO_CAMERAS (1..32) changes it for load tests.</summary>
    public static int CameraCount
    {
        get
        {
            var v = Environment.GetEnvironmentVariable("CV_DEMO_CAMERAS");
            return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 32 ? n : Names.Length;
        }
    }

    public static string Name(int channel) =>
        channel >= 1 && channel <= Names.Length ? Names[channel - 1] : $"Camera {channel:00}";

    /// <summary>Stable per-channel hue for the fake picture.</summary>
    public static double Hue(int channel) => channel * 47 % 360;
}
