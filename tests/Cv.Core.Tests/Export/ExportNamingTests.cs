using Cv.Core.Export;

namespace Cv.Core.Tests.Export;

public class ExportNamingTests
{
    private static readonly DateTime D = new(2026, 11, 7);

    [Fact]
    public void Whole_minutes_use_hours_and_minutes() =>
        Assert.Equal("Cổng chính_2026-11-07_14h30-14h35",
            ExportNaming.BaseName("Cổng chính", 1, D.AddHours(14).AddMinutes(30), D.AddHours(14).AddMinutes(35)));

    [Fact]
    public void Seconds_are_added_to_both_bounds_when_one_has_them() =>
        Assert.Equal("Cổng chính_2026-11-07_14h30m00-14h35m20",
            ExportNaming.BaseName("Cổng chính", 1, D.AddHours(14).AddMinutes(30), D.AddHours(14).AddMinutes(35).AddSeconds(20)));

    [Fact]
    public void Invalid_characters_are_replaced()
    {
        Assert.Equal("Cam_1_2___ x", ExportNaming.Sanitize(" Cam/1:2*?\" x. "));
        Assert.Equal("A_B_2026-11-07_00h00-01h00", ExportNaming.BaseName("A\\B", 1, D, D.AddHours(1)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("...")]
    public void A_missing_name_uses_the_camera_number(string? name) =>
        Assert.Equal("Camera 05_2026-11-07_23h50-00h10", ExportNaming.BaseName(name, 5, D.AddHours(23).AddMinutes(50), D.AddDays(1).AddMinutes(10)));

    [Fact]
    public void Very_long_names_are_shortened() =>
        Assert.Equal(80, ExportNaming.BaseName(new string('x', 300), 1, D, D.AddHours(1)).Split('_')[0].Length);
}
