namespace Cv.Core.Tests;

public class TimeTextTests
{
    [Fact]
    public void Clock_reads_like_the_design()
    {
        var t = new DateTime(2026, 11, 7, 14, 32, 5);
        Assert.Equal("14:32:05 — Thứ Bảy 07/11/2026", TimeText.Clock(t));
        Assert.Equal("Chủ Nhật", TimeText.DayName(t.AddDays(1)));
        Assert.Equal("07/11/2026", TimeText.Date(t));
    }

    [Theory]
    [InlineData(0, "1 KB")]
    [InlineData(524288, "512 KB")]
    [InlineData(12_897_484, "12,3 MB")]
    [InlineData(1_342_177_280, "1,25 GB")]
    public void Sizes_use_a_decimal_comma(long bytes, string text) => Assert.Equal(text, TimeText.Size(bytes));
}
