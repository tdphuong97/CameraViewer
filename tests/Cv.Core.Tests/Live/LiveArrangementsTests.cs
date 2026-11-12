using Cv.App.Logic;
using Cv.Core.Settings;

namespace Cv.Core.Tests.Live;

public class LiveArrangementsTests
{
    static AppSettings Old() => new() { LiveGrid = 16, LiveOrder = [new CameraRef("n", 3), new CameraRef("n", 1)] };

    [Fact]
    public void Settings_from_an_older_version_are_the_default_arrangement()
    {
        var s = Old();
        var a = LiveArrangements.Active(s);
        Assert.Equal((LiveArrangements.DefaultName, 16), (a.Name, a.Grid));
        Assert.Equal(s.LiveOrder, a.Order);
        Assert.Empty(a.Hidden);
        Assert.True(LiveArrangements.IsDefault(s));
        Assert.Equal([LiveArrangements.DefaultName], LiveArrangements.Names(s));
    }

    [Fact]
    public void Edits_go_to_the_arrangement_in_use_and_only_there()
    {
        var s = Old();
        Assert.Null(LiveArrangements.AddFromActive(s, "Trực đêm"));
        LiveArrangements.SaveActive(s, 105, [new CameraRef("n", 7)], [new CameraRef("n", 9)]);
        Assert.Equal(16, s.LiveGrid); // the default is untouched
        Assert.Equal(105, s.LivePresets[0].Grid);
        Assert.Equal(105, LiveArrangements.Active(s).Grid);

        Assert.True(LiveArrangements.Switch(s, LiveArrangements.DefaultName));
        LiveArrangements.SaveActive(s, 4, [], []);
        Assert.Equal(4, s.LiveGrid);
        Assert.Equal(105, s.LivePresets[0].Grid);
    }

    [Fact]
    public void A_new_arrangement_starts_as_a_copy_of_the_one_in_use_and_becomes_the_active_one()
    {
        var s = Old();
        s.LiveHidden = [new CameraRef("n", 5)];
        Assert.Null(LiveArrangements.AddFromActive(s, "  Cổng và bãi xe "));
        Assert.Equal("Cổng và bãi xe", s.LivePreset);
        var a = LiveArrangements.Active(s);
        Assert.Equal((16, 2, 1), (a.Grid, a.Order.Count, a.Hidden.Count));
        a.Order.Clear(); // a copy: the stored one is not changed
        Assert.Equal(2, LiveArrangements.Active(s).Order.Count);
    }

    [Theory]
    [InlineData("", "Hãy nhập tên cho bộ.")]
    [InlineData("   ", "Hãy nhập tên cho bộ.")]
    [InlineData("mặc định", "Tên này đã có, hãy chọn tên khác.")]
    [InlineData("TRỰC ĐÊM", "Tên này đã có, hãy chọn tên khác.")]
    [InlineData("Một cái tên dài hơn hai mươi bốn ký tự", "Tên dài quá 24 ký tự.")]
    public void Bad_names_are_refused_with_a_message(string name, string message)
    {
        var s = Old();
        LiveArrangements.AddFromActive(s, "Trực đêm");
        Assert.Equal(message, LiveArrangements.AddFromActive(s, name));
        Assert.Single(s.LivePresets);
    }

    [Fact]
    public void Rename_keeps_the_content_and_allows_the_same_name_in_another_case()
    {
        var s = Old();
        Assert.Equal("Bộ Mặc định không đổi tên được.", LiveArrangements.RenameActive(s, "X"));
        LiveArrangements.AddFromActive(s, "Trực đêm");
        LiveArrangements.AddFromActive(s, "Ban ngày");
        Assert.Equal("Tên này đã có, hãy chọn tên khác.", LiveArrangements.RenameActive(s, "trực đêm"));
        Assert.Null(LiveArrangements.RenameActive(s, "BAN NGÀY"));
        Assert.Equal("BAN NGÀY", s.LivePreset);
        Assert.Equal(["Trực đêm", "BAN NGÀY"], s.LivePresets.Select(p => p.Name));
    }

    [Fact]
    public void Delete_returns_to_the_default_and_never_deletes_the_default()
    {
        var s = Old();
        Assert.Null(LiveArrangements.DeleteActive(s));
        LiveArrangements.AddFromActive(s, "Trực đêm");
        Assert.Equal("Trực đêm", LiveArrangements.DeleteActive(s));
        Assert.Empty(s.LivePresets);
        Assert.True(LiveArrangements.IsDefault(s));
        Assert.Equal(16, LiveArrangements.Active(s).Grid);
    }

    [Fact]
    public void Switching_to_an_unknown_name_changes_nothing()
    {
        var s = Old();
        LiveArrangements.AddFromActive(s, "Trực đêm");
        Assert.False(LiveArrangements.Switch(s, "Không có"));
        Assert.Equal("Trực đêm", s.LivePreset);
    }

    [Fact]
    public void All_lists_the_default_first()
    {
        var s = Old();
        LiveArrangements.AddFromActive(s, "A");
        LiveArrangements.AddFromActive(s, "B");
        Assert.Equal([LiveArrangements.DefaultName, "A", "B"], LiveArrangements.All(s).Select(a => a.Name));
    }
}
