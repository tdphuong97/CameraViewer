using Cv.Core.Hik;
using Cv.Core.Nvr;

namespace Cv.Diagnostics.Tests;

/// <summary>The probe's information line about people detections.</summary>
public sealed class ProbePeopleTests : IDisposable
{
    private const string Password = "Mock@1234#!";
    private const string Step = "Tìm lúc có người";
    private static readonly DateTimeOffset Started = new(2026, 11, 7, 19, 45, 10, TimeSpan.FromHours(7));

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cv-probe-people-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeNvr _nvr = new();

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private Task<ProbeReport> RunAsync(bool diagnostics = true) =>
        new ProbeRunner(new FakeNvrFactory(_nvr, diagnostics), new FakeVideoFactory(), new FakeMedia())
        {
            Time = new FixedTime(Started),
            Timeouts = new ProbeTimeouts { Watch = TimeSpan.FromMilliseconds(30), Poll = TimeSpan.FromMilliseconds(2), VideoOpen = TimeSpan.FromSeconds(2) },
        }.RunAsync(new NvrConfig { Host = FakeNvr.Host, User = "admin" }, Password, _dir, null, default);

    private static string Line(ProbeReport r) => r.Text.Split('\n').Single(l => l.Contains(Step) && l.Contains('|')).TrimEnd('\r');

    private List<string> PeopleCalls => _nvr.Calls.Where(c => c.StartsWith("people", StringComparison.Ordinal) || c == "picture").ToList();

    [Fact]
    public async Task Counts_every_channel_at_once_then_checks_the_busiest_and_one_picture()
    {
        var r = await RunAsync();

        Assert.True(r.Passed, r.Text);
        Assert.Equal(["peopleOverview", "people 14", "picture"], PeopleCalls);
        var line = Line(r);
        Assert.Contains("[PASS]", line);
        Assert.StartsWith("    [PASS]   " + Step, line); // an information line under the recording search
        Assert.Contains("có: 60 lần trong 24 giờ qua, kênh D8 D14", line);
        Assert.Contains("kênh D14: 40 lần, gần nhất 2026-11-07 17:45:51", line);
        Assert.Contains("ảnh lúc 17:45:51: 244.1 KB JPEG", line);
        Assert.Contains("Lúc có người", r.Text);
        Assert.Contains("60 lần trong 24 giờ qua, kênh D8 D14; ảnh được (244.1 KB)", r.Text);
        Assert.True(r.Text.IndexOf("Tìm ghi hình 24 giờ qua", StringComparison.Ordinal) < r.Text.IndexOf(line, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_first_page_that_is_full_says_there_are_more()
    {
        _nvr.Overview = _ => new PeopleOverview(
            Enumerable.Range(0, 100).Select(i => new DetectionEvent(i % 2 == 0 ? 13 : 8, FakeNvr.Now.AddHours(-3).AddSeconds(i), null)).ToList(), 100, true);

        var r = await RunAsync();

        Assert.Contains("có: 100+ lần trong 24 giờ qua, kênh D8 D13 (theo 100 lần đầu)", Line(r));
        Assert.Equal("people 8", PeopleCalls[1]); // a tie goes to the lower channel
    }

    [Fact]
    public async Task No_detections_passes_with_a_hint_about_the_cameras()
    {
        _nvr.People = _ => [];

        var r = await RunAsync();

        Assert.True(r.Passed, r.Text);
        var line = Line(r);
        Assert.Contains("[PASS]", line);
        Assert.Contains("không thấy lần nào trong 24 giờ qua trên 17 camera", line);
        Assert.Contains("chưa bật phát hiện người", line);
        Assert.Equal(["peopleOverview"], PeopleCalls);
    }

    [Fact]
    public async Task Recorder_without_people_search_is_reported_but_the_probe_still_passes()
    {
        _nvr.Overview = _ => throw new NvrDetectionUnsupportedException("Đầu ghi này không cho tìm lúc có người.");

        var r = await RunAsync();

        Assert.True(r.Passed, r.Text);
        Assert.Contains("KẾT QUẢ: ĐẠT", r.Text);
        var line = Line(r);
        Assert.Contains("[FAIL]", line);
        Assert.Contains("Đầu ghi này không cho tìm lúc có người.", line);
        Assert.Contains("đầu ghi không hỗ trợ", r.Text);
        Assert.Equal(["peopleOverview"], PeopleCalls);
    }

    [Fact]
    public async Task Refused_overview_still_checks_the_recording_channel_the_app_way()
    {
        _nvr.Overview = _ => throw new NvrException("Đầu ghi từ chối thao tác này.");
        _nvr.People = ch => ch == 1 ? [new DetectionEvent(1, FakeNvr.Now.AddHours(-1), "/picture/Streaming/tracks/103/?name=a")] : [];

        var r = await RunAsync();

        Assert.Equal(["peopleOverview", "people 1", "picture"], PeopleCalls);
        var line = Line(r);
        Assert.Contains("[PASS]", line);
        Assert.Contains("tìm mọi kênh một lần không được (Đầu ghi từ chối thao tác này.)", line);
        Assert.Contains("kênh D1: 1 lần", line);
    }

    [Fact]
    public async Task Picture_that_cannot_be_fetched_is_information_only()
    {
        _nvr.Picture = _ => throw new NvrException("Đầu ghi không trả về ảnh của lần phát hiện này.");

        var r = await RunAsync();

        Assert.True(r.Passed, r.Text);
        var line = Line(r);
        Assert.Contains("[FAIL]", line);
        Assert.Contains("Đầu ghi không trả về ảnh của lần phát hiện này.", line);
        Assert.Contains("ảnh KHÔNG tải được", r.Text);
    }

    [Fact]
    public async Task Detections_without_pictures_pass()
    {
        _nvr.People = ch => ch == 14 ? [new DetectionEvent(14, FakeNvr.Now.AddHours(-1), null)] : [];

        var r = await RunAsync();

        Assert.Contains("đầu ghi không lưu ảnh kèm theo", Line(r));
        Assert.DoesNotContain("picture", PeopleCalls);
    }

    [Fact]
    public async Task Recorder_lost_during_the_people_search_stops_the_probe_and_fails_it()
    {
        _nvr.People = _ => throw new NvrUnreachableException("Không kết nối được đầu ghi.");

        var r = await RunAsync();

        Assert.False(r.Passed);
        Assert.Contains("CÓ LỖI", r.Text);
        Assert.Matches(@"\d\. \[FAIL\] Tìm lúc có người", Line(r)); // a numbered step, not an information line
        Assert.DoesNotContain(_nvr.Calls, c => c.StartsWith("download", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Client_without_people_search_skips_the_line()
    {
        var r = await RunAsync(diagnostics: false);

        Assert.Contains("[SKIP]", Line(r));
        Assert.Empty(PeopleCalls);
    }

    [Fact]
    public async Task Password_is_not_in_the_people_line()
    {
        _nvr.Picture = _ => throw new NvrException($"lỗi http://admin:{Password}@{FakeNvr.Host}/picture mật khẩu {Password}");

        var r = await RunAsync();

        Assert.DoesNotContain(Password, r.Text);
        Assert.DoesNotContain(Password, File.ReadAllText(r.SavedPath!));
    }
}
