using System.Globalization;

namespace MockNvr;

public sealed record MockChannel(int No, string Name, string Ip, string Model, bool Online);

/// <summary>The fixed identity of the fake recorder: model, serial, channels.</summary>
public static class MockDevice
{
    public const string Model = "DS-7732NI-M4";
    public const string Serial = "DS-7732NI-M40820230815CCRRL12345678WCVU";
    public const string DeviceId = "48443934-3537-3433-3732-c42f90e1a2b3";
    public const string Mac = "c4:2f:90:e1:a2:b3";
    public const string Firmware = "V4.71.120";
    public const string FirmwareDate = "build 231208";
    public const int OfflineChannel = 5;
    public const int Fps = 15;

    /// <summary>Generic Vietnamese names (UTF-8 on purpose: real recorders keep what the installer typed).</summary>
    private static readonly string[] Names =
    [
        "Cổng chính", "Sảnh tầng 1", "Lễ tân", "Hành lang tầng 1", "Kho hàng", "Hầm xe B1", "Hầm xe B2",
        "Thang máy 1", "Thang máy 2", "Cầu thang bộ", "Sân sau", "Lối thoát hiểm", "Phòng họp",
        "Hành lang tầng 2", "Khu để xe máy", "Cổng phụ", "Sân thượng",
    ];

    private static readonly string[] Models = ["DS-2CD1043G2-LIU", "DS-2CD2143G2-I", "DS-2CD1023G0E-I", "DS-2CD2T47G2-L"];

    public static IReadOnlyList<MockChannel> Channels { get; } = Names
        .Select((n, i) => new MockChannel(i + 1, n, $"192.168.254.{i + 2}", Models[i % Models.Length], i + 1 != OfflineChannel))
        .ToArray();

    public static MockChannel? Channel(int no) => no >= 1 && no <= Channels.Count ? Channels[no - 1] : null;
}

/// <summary>The recorder's wall clock (time zone + optional skew), Kind Unspecified like the contracts.</summary>
public sealed class MockClock(MockOptions options, TimeProvider time)
{
    public TimeSpan Offset => options.UtcOffset;

    public DateTime UtcNow => (time.GetUtcNow() + options.ClockSkew).UtcDateTime;

    public DateTime LocalNow => DateTime.SpecifyKind(UtcNow + Offset, DateTimeKind.Unspecified);

    public DateTime ToLocal(DateTime utc) => DateTime.SpecifyKind(utc + Offset, DateTimeKind.Unspecified);

    public DateTime ToUtc(DateTime local) => DateTime.SpecifyKind(local - Offset, DateTimeKind.Utc);

    /// <summary>"+07:00".</summary>
    public string OffsetText => FormatOffset(Offset);

    /// <summary>POSIX TZ as Hikvision writes it: UTC+7 is "CST-7:00:00" (sign inverted).</summary>
    public string PosixZone
    {
        get
        {
            var o = Offset;
            var sign = o < TimeSpan.Zero ? "+" : "-";
            o = o.Duration();
            return string.Create(CultureInfo.InvariantCulture, $"CST{sign}{o.Hours}:{o.Minutes:00}:00");
        }
    }

    public static string FormatOffset(TimeSpan o) =>
        (o < TimeSpan.Zero ? "-" : "+") + o.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);
}
