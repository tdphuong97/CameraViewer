using Cv.Core.Hik;
using Cv.Core.Nvr;

namespace Cv.Core.Tests.Hik;

public class HikTimeTests
{
    [Theory]
    [InlineData("CST-7:00:00", 7, 0)]
    [InlineData("CST-7", 7, 0)]
    [InlineData("CST+5:00:00", -5, 0)]
    [InlineData("EST5EDT,M3.2.0,M11.1.0", -5, 0)]
    [InlineData("<+07>-7", 7, 0)]
    [InlineData("IST-5:30:00", 5, 30)]
    [InlineData("UTC0", 0, 0)]
    [InlineData("GMT+0", 0, 0)]
    public void Posix_time_zones_invert_the_sign(string tz, int hours, int minutes) =>
        Assert.Equal(new TimeSpan(hours, minutes, 0), HikTime.ParsePosixOffset(tz));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("+07:00")]
    [InlineData("CST-99")]
    public void Unreadable_time_zones_are_null(string? tz) => Assert.Null(HikTime.ParsePosixOffset(tz));

    [Fact]
    public void Iso_times_parse_with_every_suffix()
    {
        Assert.True(HikTime.TryParse("2026-11-07T14:30:05Z", out var z));
        Assert.Equal((new DateTime(2026, 11, 7, 14, 30, 5), StampKind.Z), (z.Wall, z.Kind));
        Assert.Equal(DateTimeKind.Unspecified, z.Wall.Kind);

        Assert.True(HikTime.TryParse("2026-11-07T14:30:05+07:00", out var o));
        Assert.Equal((StampKind.Offset, TimeSpan.FromHours(7)), (o.Kind, o.Offset));
        Assert.Equal(new DateTime(2026, 11, 7, 7, 30, 5), o.Raw);

        Assert.True(HikTime.TryParse("2026-11-07T14:30:05-0330", out var neg));
        Assert.Equal(new TimeSpan(-3, -30, 0), neg.Offset);

        Assert.True(HikTime.TryParse("2026-11-07 14:30:05.123", out var none));
        Assert.Equal((new DateTime(2026, 11, 7, 14, 30, 5), StampKind.None), (none.Wall, none.Kind));

        Assert.False(HikTime.TryParse("20261107T143005Z", out _));
        Assert.False(HikTime.TryParse("2026-13-07T14:30:05Z", out _));
        Assert.False(HikTime.TryParse(null, out _));
    }

    [Fact]
    public void Conversions_follow_the_time_mode()
    {
        var off = TimeSpan.FromHours(7);
        var local = new DateTime(2026, 11, 7, 14, 30, 0);
        Assert.Equal(local, HikTime.ToWire(local, TimeMode.LocalWithZ, off));
        Assert.Equal(new DateTime(2026, 11, 7, 7, 30, 0), HikTime.ToWire(local, TimeMode.Utc, off));

        HikTime.TryParse("2026-11-07T07:30:00Z", out var z);
        Assert.Equal(new DateTime(2026, 11, 7, 7, 30, 0), HikTime.ToLocal(z, TimeMode.LocalWithZ, off));
        Assert.Equal(local, HikTime.ToLocal(z, TimeMode.Utc, off));

        HikTime.TryParse("2026-11-07T07:30:00+00:00", out var utc);
        Assert.Equal(local, HikTime.ToLocal(utc, TimeMode.LocalWithZ, off));
        HikTime.TryParse("2026-11-07T14:30:00+07:00", out var same);
        Assert.Equal(local, HikTime.ToLocal(same, TimeMode.Utc, off));
    }

    [Fact]
    public void Wire_formats_match_the_recorder()
    {
        var t = new DateTime(2026, 11, 7, 9, 5, 3);
        Assert.Equal("2026-11-07T09:05:03Z", HikTime.IsoZ(t));
        Assert.Equal("20261107T090503Z", HikTime.UriZ(t));
    }
}
