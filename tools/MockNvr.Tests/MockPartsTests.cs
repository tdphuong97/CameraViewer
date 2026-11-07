namespace MockNvr.Tests;

public sealed class MockPartsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 19, 45, 12, TimeSpan.FromHours(7));
    private static readonly MockOptions Opt = new() { UtcOffset = TimeSpan.FromHours(7) };
    private static MockClock Clock => new(Opt, new FixedTime(Now));
    private static DateTime Local(int day, int h, int m = 0, int s = 0) => new(2026, 11, day, h, m, s);

    [Theory]
    [InlineData(TimeWriteMode.LocalZ, "2026-11-07T10:00:00Z", "20261107T100000Z")]
    [InlineData(TimeWriteMode.Utc, "2026-11-07T03:00:00Z", "20261107T030000Z")]
    [InlineData(TimeWriteMode.Offset, "2026-11-07T10:00:00+07:00", "20261107T030000Z")]
    public void Times_are_written_like_the_firmware(TimeWriteMode mode, string iso, string compact)
    {
        var t = new HikTime(Clock, mode);
        Assert.Equal(iso, t.FormatIso(Local(7, 10)));
        Assert.Equal(compact, t.FormatCompact(Local(7, 10)));
        Assert.True(t.TryParse(iso, out var back));
        Assert.Equal(Local(7, 10), back);
        Assert.True(t.TryParse(compact, out back));
        Assert.Equal(Local(7, 10), back);
    }

    [Fact]
    public void Explicit_offsets_are_converted_to_device_time()
    {
        var t = new HikTime(Clock, TimeWriteMode.LocalZ);
        Assert.True(t.TryParse("2026-11-07T03:00:00+00:00", out var v));
        Assert.Equal(Local(7, 10), v);
        Assert.False(t.TryParse("yesterday", out _));
    }

    [Fact]
    public void Clock_and_time_zone_look_like_hikvision()
    {
        Assert.Equal("CST-7:00:00", Clock.PosixZone);
        Assert.Equal("+07:00", Clock.OffsetText);
        Assert.Equal(Local(7, 19, 45, 12), Clock.LocalNow);
        Assert.Contains("<localTime>2026-11-07T19:45:12+07:00</localTime>", IsapiXml.Time(Clock));
    }

    [Fact]
    public void A_day_of_recordings_has_hour_files_the_night_gap_and_ends_now()
    {
        var store = new RecordingStore(Clock);
        var segs = store.Find(1, Local(7, 0), Local(7, 23, 59, 59));

        Assert.True(segs[0].Start < Local(7, 0), "the first file started the evening before");
        Assert.Equal(Local(7, 19, 45, 12), segs[^1].End);
        Assert.DoesNotContain(segs, s => s.Start < Local(7, 2, 30) && s.End > Local(7, 2));
        Assert.All(segs, s => Assert.True(s.Duration <= TimeSpan.FromMinutes(63)));
        for (var i = 1; i < segs.Count; i++) Assert.True(segs[i].Start >= segs[i - 1].End);
        Assert.Equal(segs.Count, segs.Select(s => s.Name).Distinct().Count());
        Assert.All(segs, s => Assert.Equal(s, store.FindByName(s.Name)));
    }

    [Fact]
    public void Offline_channel_has_nothing_in_the_last_day_and_nothing_is_older_than_a_week()
    {
        var store = new RecordingStore(Clock);
        Assert.Empty(store.Find(MockDevice.OfflineChannel, Local(6, 19, 46), Local(7, 23)));
        Assert.NotEmpty(store.Find(MockDevice.OfflineChannel, Local(5, 0), Local(6, 19)));
        // Kept 7 days: the oldest file is the one running at 2026-10-31 19:45.
        Assert.Empty(store.Find(1, new DateTime(2026, 10, 30, 0, 0, 0), new DateTime(2026, 10, 31, 18, 0, 0)));
        Assert.NotEmpty(store.Find(1, new DateTime(2026, 10, 31, 0, 0, 0), new DateTime(2026, 10, 31, 21, 0, 0)));
        Assert.Empty(store.Find(18, Local(7, 0), Local(7, 12)));
    }

    [Fact]
    public void Channels_are_seventeen_with_channel_five_offline()
    {
        Assert.Equal(17, MockDevice.Channels.Count);
        Assert.Equal([5], MockDevice.Channels.Where(c => !c.Online).Select(c => c.No));
        var xml = IsapiXml.InputProxyStatus();
        Assert.Equal(16, xml.Split("<online>true</online>").Length - 1);
        Assert.Contains("<name>Cổng chính</name>", IsapiXml.InputProxyChannels());
        Assert.Contains("<id>1702</id>", IsapiXml.StreamingChannels());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Annex_b_is_split_into_access_units_with_or_without_delimiters(bool aud)
    {
        var h264 = ElementaryClip.Parse(Clips.H264(60, aud), VideoCodec.H264);
        var h265 = ElementaryClip.Parse(Clips.H265(60, aud), VideoCodec.H265);
        foreach (var clip in new[] { h264, h265 })
        {
            Assert.Equal(60, clip.Units.Count);
            Assert.Equal([0, 30], clip.Units.Select((u, i) => (u, i)).Where(x => x.u.Key).Select(x => x.i));
            Assert.All(clip.Units, u => Assert.Equal(new byte[] { 0, 0, 0, 1 }, u.Data[..4].ToArray()));
        }
        // Delimiters are dropped (Hikvision streams carry none).
        Assert.DoesNotContain(h264.Units, u => u.Data.Span.IndexOf(new byte[] { 0, 0, 0, 1, 0x09 }) >= 0);
    }

    [Theory]
    [InlineData(VideoCodec.H265, (byte)0x24)]
    [InlineData(VideoCodec.H264, (byte)0x1B)]
    public async Task Program_stream_has_imkh_header_psm_and_continuous_timestamps(VideoCodec codec, byte streamType)
    {
        var clip = ElementaryClip.Parse(codec == VideoCodec.H265 ? Clips.H265(45) : Clips.H264(45), codec);
        using var ms = new MemoryStream();
        var written = await HikPsWriter.WriteAsync(ms, clip, TimeSpan.FromSeconds(10), 36000L * 90000, audio: true, 0, CancellationToken.None);

        var bytes = ms.ToArray();
        Assert.Equal(written, bytes.Length);
        Assert.Equal("IMKH"u8.ToArray(), bytes[..4]);
        var ps = PsInfo.Read(bytes);
        Assert.Equal(streamType, ps.VideoStreamType);
        Assert.True(ps.HasAudioInPsm);
        Assert.True(ps.PsmCrcOk);
        Assert.Equal(150, ps.VideoFrames);
        Assert.Equal(36000L * 90000, ps.FirstPts);
        Assert.Equal(149 * 6000, ps.LastPts - ps.FirstPts);
        Assert.Equal(7, ps.KeyFramesWithPsm); // 45-frame loop with keys at 0 and 30: frames 0, 30, 45, 75, 90, 120, 135
        Assert.InRange(ps.AudioPes, 495, 505);
    }

    [Fact]
    public async Task Large_frames_are_split_over_several_pes_packets_and_audio_can_be_off()
    {
        var bytes = new List<byte>();
        bytes.AddRange(new byte[] { 0, 0, 0, 1, 0x67, 0x4D, 0, 0, 0, 1, 0x68, 0xEE, 0, 0, 0, 1, 0x65, 0x88 });
        bytes.AddRange(Enumerable.Repeat((byte)0x55, 40_000));
        var clip = ElementaryClip.Parse([.. bytes], VideoCodec.H264);
        using var ms = new MemoryStream();
        await HikPsWriter.WriteAsync(ms, clip, TimeSpan.FromSeconds(1), 90000, audio: false, 0, CancellationToken.None);

        var ps = PsInfo.Read(ms.ToArray());
        Assert.Equal(15, ps.VideoFrames);
        Assert.Equal(15 * 3, ps.VideoPes);
        Assert.Equal(0, ps.AudioPes);
        Assert.False(ps.HasAudioInPsm);
    }

    [Fact]
    public void Command_line_is_parsed()
    {
        var o = MockOptions.Parse(["--port", "9090", "--time-mode=utc", "--download", "xml-error", "--download-codec", "h264", "--utc-offset", "-05:30", "--no-audio"], out var err);
        Assert.Null(err);
        Assert.Equal(9090, o!.Port);
        Assert.Equal(TimeWriteMode.Utc, o.TimeMode);
        Assert.Equal(DownloadMode.XmlError, o.Download);
        Assert.Equal(VideoCodec.H264, o.DownloadCodec);
        Assert.Equal(new TimeSpan(-5, -30, 0), o.UtcOffset);
        Assert.False(o.Audio);
        Assert.Null(MockOptions.Parse(["--bogus"], out err));
        Assert.NotNull(err);
    }
}

internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
}
