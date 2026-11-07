using System.Net;
using System.Net.Http;
using Cv.Core.Hik;
using Cv.Core.Nvr;
using Cv.Core.Tests.Fakes;

namespace Cv.Core.Tests.Hik;

public class DownloadTests : IDisposable
{
    private static readonly DateTime From = new(2026, 11, 7, 10, 0, 0);
    private static readonly DateTime To = new(2026, 11, 7, 10, 10, 0);
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    /// <summary>Two 1-hour files around the range (09:30-10:05, 10:05-11:00).</summary>
    private static FakeRecorder Recorder() => new FakeRecorder()
        .Add(101, "2026-11-07T09:30:00Z", "2026-11-07T10:05:00Z")
        .Add(101, "2026-11-07T10:05:00Z", "2026-11-07T11:00:00Z");

    private static HttpResponseMessage RefuseXml(FakeIsapiHandler.Seen s, CancellationToken ct) => FakeRecorder.Xml(Samples.NotSupported);

    private string[] Files() => Directory.GetFiles(_dir.Path);

    [Fact]
    public async Task By_time_get_with_body_works_first()
    {
        var f = Recorder();
        var ps = Samples.Ps(5000);
        f.Download = (_, _) => FakeIsapiHandler.Bytes(ps);
        using var c = f.Client();
        var progress = new List<long>();
        var parts = await c.DownloadAsync(1, From, To, _dir.Path, new SyncProgress<long>(progress.Add), default);

        var p = Assert.Single(parts);
        Assert.Equal(DownloadMethod.ByTime, p.Method);
        Assert.Equal(Path.Combine(_dir.Path, "ch1_20261107100000_1.ps"), p.FilePath);
        Assert.Equal((From, From, To), (p.NominalStart, p.CoveredFrom, p.CoveredTo));
        Assert.Equal(ps, File.ReadAllBytes(p.FilePath));
        Assert.Equal(ps.Length, progress[^1]);

        var (verb, uri) = Assert.Single(f.Downloads);
        Assert.Equal(HttpMethod.Get, verb);
        Assert.Equal("rtsp://192.168.1.64/Streaming/tracks/101/?starttime=20261107T100000Z&endtime=20261107T101000Z", uri);
        var req = f.Handler.Requests.Last(r => r.PathAndQuery == "/ISAPI/ContentMgmt/download");
        Assert.Contains("xmlns=\"http://www.isapi.org/ver20/XMLSchema\"", req.Body);
        Assert.Equal(DownloadMethod.ByTime, c.WorkingMethod);
    }

    [Fact]
    public async Task By_time_falls_back_to_post_and_remembers_it()
    {
        var f = Recorder();
        f.Download = (s, _) => s.Method == HttpMethod.Post ? FakeIsapiHandler.Bytes(Samples.Ps()) : FakeRecorder.Xml(Samples.NotSupported);
        using var c = f.Client();
        var parts = await c.DownloadAsync(1, From, To, _dir.Path, null, default);
        Assert.Equal(DownloadMethod.ByTime, Assert.Single(parts).Method);
        Assert.Equal([HttpMethod.Get, HttpMethod.Post], f.Downloads.Select(d => d.Method));

        f.Downloads.Clear();
        await c.DownloadAsync(1, From, To, _dir.Path, null, default);
        Assert.Equal([HttpMethod.Post], f.Downloads.Select(d => d.Method));
    }

    [Fact]
    public async Task Segment_range_narrows_each_segment_uri_and_keeps_name_and_size()
    {
        var f = Recorder();
        f.Download = (s, _) => FakeRecorder.PlaybackUriOf(s.Body).Contains("name=")
            ? FakeIsapiHandler.Bytes(Samples.Ps())
            : FakeRecorder.Xml(Samples.NotSupported);
        using var c = f.Client();
        var parts = await c.DownloadAsync(1, From, To, _dir.Path, null, default);

        Assert.Equal(2, parts.Count);
        Assert.All(parts, p => Assert.Equal(DownloadMethod.BySegmentRange, p.Method));
        Assert.Equal((From, From, From.AddMinutes(5)), (parts[0].NominalStart, parts[0].CoveredFrom, parts[0].CoveredTo));
        Assert.Equal((From.AddMinutes(5), From.AddMinutes(5), To), (parts[1].NominalStart, parts[1].CoveredFrom, parts[1].CoveredTo));
        var segUris = f.Downloads.Where(d => d.Uri.Contains("name=")).Select(d => d.Uri).Distinct().ToList();
        Assert.Equal([
            "rtsp://192.168.1.64/Streaming/tracks/101/?starttime=20261107T100000Z&endtime=20261107T100500Z&name=00000&size=524288000",
            "rtsp://192.168.1.64/Streaming/tracks/101/?starttime=20261107T100500Z&endtime=20261107T101000Z&name=00001&size=524288000",
        ], segUris);
        Assert.Equal(["ch1_20261107100000_1.ps", "ch1_20261107100000_2.ps"], parts.Select(p => Path.GetFileName(p.FilePath)));
        Assert.Equal(DownloadMethod.BySegmentRange, c.WorkingMethod);
    }

    [Fact]
    public async Task Whole_segment_sends_the_search_uri_untouched()
    {
        var f = Recorder();
        // Only the untouched URIs (starttime 0930 / 1005 with the segment end) succeed.
        f.Download = (s, _) =>
        {
            var uri = FakeRecorder.PlaybackUriOf(s.Body);
            return uri.Contains("starttime=20261107T093000Z&endtime=20261107T100500Z") || uri.Contains("starttime=20261107T100500Z&endtime=20261107T110000Z")
                ? FakeIsapiHandler.Bytes(Samples.Ps())
                : FakeIsapiHandler.Bytes("not a video at all"u8.ToArray());
        };
        using var c = f.Client();
        var parts = await c.DownloadAsync(1, From, To, _dir.Path, null, default);
        Assert.Equal(2, parts.Count);
        Assert.All(parts, p => Assert.Equal(DownloadMethod.BySegmentWhole, p.Method));
        Assert.Equal((new DateTime(2026, 11, 7, 9, 30, 0), From, From.AddMinutes(5)), (parts[0].NominalStart, parts[0].CoveredFrom, parts[0].CoveredTo));
        Assert.Equal(From.AddMinutes(5), parts[1].NominalStart);
        Assert.Equal(2, Files().Length);
    }

    [Fact]
    public async Task Nothing_working_is_download_unsupported_and_leaves_no_file()
    {
        var f = Recorder();
        f.Download = RefuseXml;
        using var c = f.Client();
        var e = await Assert.ThrowsAsync<NvrDownloadUnsupportedException>(() => c.DownloadAsync(1, From, To, _dir.Path, null, default));
        Assert.False(string.IsNullOrWhiteSpace(e.Message));
        Assert.Equal(6, f.Downloads.Count); // 3 methods x GET/POST, first part each
        Assert.Empty(Files());
        Assert.Null(c.WorkingMethod);
    }

    [Fact]
    public async Task Http_errors_and_empty_bodies_are_refusals_too()
    {
        var f = Recorder();
        var n = 0;
        f.Download = (_, _) => ++n switch
        {
            1 => FakeIsapiHandler.Text("", HttpStatusCode.MethodNotAllowed),
            2 => FakeIsapiHandler.Bytes([]),
            _ => FakeIsapiHandler.Bytes([0, 0, 1, 0xBA, 1, 2, 3]),
        };
        using var c = f.Client();
        var parts = await c.DownloadAsync(1, From, To, _dir.Path, null, default);
        Assert.Equal(DownloadMethod.BySegmentRange, parts[0].Method); // ByTime GET 405, POST empty
    }

    [Fact]
    public async Task Nothing_recorded_says_so()
    {
        var f = new FakeRecorder().Add(101, "2026-11-07T12:00:00Z", "2026-11-07T13:00:00Z");
        using var c = f.Client();
        var e = await Assert.ThrowsAsync<NvrException>(() => c.DownloadAsync(1, From, To, _dir.Path, null, default));
        Assert.Equal("Không có ghi hình trong khoảng thời gian này.", e.Message);
        Assert.Empty(f.Downloads);
    }

    [Fact]
    public async Task A_gap_splits_by_time_into_one_part_per_span()
    {
        var f = new FakeRecorder()
            .Add(101, "2026-11-07T09:00:00Z", "2026-11-07T10:02:00Z")
            .Add(101, "2026-11-07T10:05:00Z", "2026-11-07T11:00:00Z");
        using var c = f.Client();
        var parts = await c.DownloadAsync(1, From, To, _dir.Path, null, default);
        Assert.Equal([(From, From.AddMinutes(2)), (From.AddMinutes(5), To)], parts.Select(p => (p.CoveredFrom, p.CoveredTo)));
        Assert.Contains("starttime=20261107T100500Z&endtime=20261107T101000Z", f.Downloads[1].Uri);
    }

    [Fact]
    public async Task Utc_mode_writes_utc_times_in_the_by_time_uri()
    {
        var f = new FakeRecorder().Add(101, "2026-11-07T02:30:00Z", "2026-11-07T04:00:00Z");
        using var c = f.Client(TimeMode.Utc);
        var parts = await c.DownloadAsync(1, From, To, _dir.Path, null, default);
        Assert.Equal((From, To), (parts[0].CoveredFrom, parts[0].CoveredTo));
        Assert.Contains("starttime=20261107T030000Z&endtime=20261107T031000Z", f.Downloads[0].Uri);
    }

    [Fact]
    public async Task A_locked_recorder_stops_the_download_at_once()
    {
        var f = Recorder();
        f.Download = (_, _) => FakeIsapiHandler.Text(Samples.Locked, HttpStatusCode.Unauthorized);
        using var c = f.Client();
        var e = await Assert.ThrowsAsync<NvrAuthException>(() => c.DownloadAsync(1, From, To, _dir.Path, null, default));
        Assert.Contains("30 phút", e.Message);
        Assert.Single(f.Downloads);
    }

    [Fact]
    public async Task A_stalled_download_is_a_lost_connection_and_leaves_no_file()
    {
        var f = Recorder();
        f.Download = (_, _) => FakeIsapiHandler.Stalling(Samples.Ps(100));
        using var c = f.Client();
        c.StallTimeout = TimeSpan.FromMilliseconds(300);
        var e = await Assert.ThrowsAsync<NvrUnreachableException>(() => c.DownloadAsync(1, From, To, _dir.Path, null, default));
        Assert.Equal("Mất kết nối đầu ghi khi đang tải video. Kiểm tra mạng rồi bấm Thử lại.", e.Message);
        Assert.Single(f.Downloads); // not a refusal: no other method tried
        Assert.Empty(Files());
    }

    [Fact]
    public async Task Cancel_mid_stream_deletes_the_partial_file()
    {
        var f = Recorder();
        f.Download = (_, _) => FakeIsapiHandler.Stalling(Samples.Ps(100));
        using var c = f.Client();
        using var cts = new CancellationTokenSource();
        var task = c.DownloadAsync(1, From, To, _dir.Path, new SyncProgress<long>(b => { if (b > 0) cts.Cancel(); }), cts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Empty(Files());
    }

    [Fact]
    public async Task A_later_part_failing_drops_the_method_and_its_files()
    {
        var f = Recorder();
        // ByTime: one part works only via POST... then fails for everything else: BySegmentRange part 1 ok, part 2 refused.
        f.Download = (s, _) =>
        {
            var uri = FakeRecorder.PlaybackUriOf(s.Body);
            if (!uri.Contains("name=")) return FakeRecorder.Xml(Samples.NotSupported);
            if (uri.Contains("name=00001") && uri.Contains("endtime=20261107T101000Z")) return FakeRecorder.Xml(Samples.NotSupported);
            return FakeIsapiHandler.Bytes(Samples.Ps());
        };
        using var c = f.Client();
        var parts = await c.DownloadAsync(1, From, To, _dir.Path, null, default);
        Assert.All(parts, p => Assert.Equal(DownloadMethod.BySegmentWhole, p.Method));
        Assert.Equal(2, Files().Length);
    }

    [Fact]
    public void Narrow_replaces_or_adds_the_times()
    {
        Assert.Equal("rtsp://h/Streaming/tracks/101/?starttime=A&endtime=B&name=1&size=2",
            HikNvrClient.Narrow("rtsp://h/Streaming/tracks/101/?starttime=X&endtime=Y&name=1&size=2", "A", "B"));
        Assert.Equal("rtsp://h/x?name=1&starttime=A&endtime=B", HikNvrClient.Narrow("rtsp://h/x?name=1", "A", "B"));
        Assert.Equal("rtsp://h/x?starttime=A&endtime=B", HikNvrClient.Narrow("rtsp://h/x", "A", "B"));
    }

    [Fact]
    public void Program_stream_detection()
    {
        Assert.True(HikNvrClient.IsProgramStream(Samples.Ps(), 44));
        Assert.True(HikNvrClient.IsProgramStream([0, 0, 1, 0xBA], 4));
        Assert.False(HikNvrClient.IsProgramStream("<?xml"u8.ToArray(), 5));
        Assert.False(HikNvrClient.IsProgramStream([0, 0, 1, 0xBA], 3));
    }
}
