using System.Xml.Linq;
using Cv.Core.Hik;
using Cv.Core.Tests.Fakes;

namespace Cv.Core.Tests.Hik;

public class HikXmlTests
{
    private static XElement Root(string xml) => HikXml.TryParse(xml)!;

    [Fact]
    public void Device_info_sample_parses()
    {
        var i = HikXml.ParseDeviceInfo(Root(Samples.DeviceInfo));
        Assert.Equal("DS-7732NI-M4", i.Model);
        Assert.Equal("DS-7732NI-M40820230815CCRRL12345678WCVU", i.SerialNumber);
        Assert.Equal("V4.71.120 build 231208", i.Firmware);
        Assert.Equal("Network Video Recorder", i.DeviceName);
    }

    [Fact]
    public void Device_info_without_namespace_and_with_declaration_parses()
    {
        var i = HikXml.ParseDeviceInfo(Root(Samples.DeviceInfoNoNs));
        Assert.Equal("DS-7732NI-M4", i.Model);
        Assert.Equal("V4.71.120", i.Firmware);
    }

    [Fact]
    public void A_bom_and_blanks_before_the_xml_are_tolerated()
    {
        Assert.NotNull(HikXml.TryParse("﻿  \r\n" + Samples.DeviceInfo));
        Assert.NotNull(HikXml.TryParse([0xEF, 0xBB, 0xBF, .. "<a/>"u8.ToArray()]));
        Assert.Null(HikXml.TryParse("IMKH...."));
        Assert.Null(HikXml.TryParse("<broken"));
    }

    [Fact]
    public void Time_sample_parses()
    {
        var (local, tz) = HikXml.ParseTime(Root(Samples.Time));
        Assert.Equal("2026-11-07T19:45:12+07:00", local);
        Assert.Equal("CST-7:00:00", tz);
    }

    [Fact]
    public void Input_proxy_channels_parse_with_and_without_namespace()
    {
        var list = HikXml.ParseInputProxyChannels(Root(Samples.Channels));
        Assert.Equal([3, 1, 2], list.Select(c => c.No));
        var c1 = list.Single(c => c.No == 1);
        Assert.Equal("Cong chinh", c1.Name);
        Assert.Equal("192.168.254.2", c1.Ip);
        Assert.Equal("DS-2CD1043G2-LIU", c1.Model);
        Assert.Null(list.Single(c => c.No == 2).Name);

        var plain = HikXml.ParseInputProxyChannels(Root(Samples.ChannelsNoNs));
        Assert.Equal("Cong chinh", Assert.Single(plain).Name);
    }

    [Fact]
    public void Channel_status_uses_online_then_detect_result()
    {
        var s = HikXml.ParseChannelStatus(Root(Samples.Status));
        Assert.True(s[1]);
        Assert.False(s[2]);
        Assert.True(s[3]);
    }

    [Fact]
    public void Streaming_channel_names_map_main_stream_ids_to_channels()
    {
        var n = HikXml.ParseStreamingChannelNames(Root(Samples.StreamingChannels));
        Assert.Equal("Camera 01", n[1]);
        Assert.Equal("Sanh tang 1", n[2]);
        Assert.Equal("Ham xe B1", n[3]);
    }

    [Fact]
    public void Search_result_sample_parses_and_unescapes_the_uri()
    {
        var p = HikXml.ParseSearchResult(Root(Samples.SearchPage));
        Assert.Equal("MORE", p.Status);
        Assert.Equal(50, p.NumOfMatches);
        var i = Assert.Single(p.Items);
        Assert.Equal("2026-11-07T00:00:00Z", i.Start);
        Assert.Equal("2026-11-07T01:02:11Z", i.End);
        Assert.Equal("rtsp://192.168.1.64/Streaming/tracks/101/?starttime=20261107T000000Z&endtime=20261107T010211Z&name=00000000123000000&size=524288000", i.PlaybackUri);
    }

    [Fact]
    public void Search_result_without_namespace_parses()
    {
        var xml = Samples.SearchPage.Replace(" xmlns=\"http://www.hikvision.com/ver20/XMLSchema\"", "");
        var p = HikXml.ParseSearchResult(Root(xml));
        Assert.Single(p.Items);
        Assert.Equal("MORE", p.Status);
    }

    [Fact]
    public void Response_status_parses_only_for_its_own_root()
    {
        var s = HikXml.ParseResponseStatus(Root(Samples.NotSupported))!;
        Assert.Equal(4, s.StatusCode);
        Assert.Equal("Invalid Operation", s.StatusString);
        Assert.Equal("notSupport", s.SubStatusCode);
        Assert.Null(HikXml.ParseResponseStatus(Root(Samples.DeviceInfo)));
    }

    [Fact]
    public void Login_counters_parse_with_namespace()
    {
        var s = HikXml.ParseLoginStatus(Root(Samples.Locked));
        Assert.Equal("lock", s.LockStatus);
        Assert.Equal(1785, s.UnlockSeconds);
        Assert.Equal(0, s.RetriesLeft);
    }

    [Fact]
    public void Search_request_has_the_hikvision_shape()
    {
        var xml = HikXml.BuildSearchRequest("C9A2C7B1-6E2D-4F1A-9B0E-3D7C2A1F5E44", 101, "2026-11-07T00:00:00Z", "2026-11-07T23:59:59Z", 50, 100);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", xml);
        Assert.Contains("<CMSearchDescription><searchID>C9A2C7B1-6E2D-4F1A-9B0E-3D7C2A1F5E44</searchID>", xml);
        Assert.Contains("<trackList><trackID>101</trackID></trackList>", xml);
        Assert.Contains("<timeSpan><startTime>2026-11-07T00:00:00Z</startTime><endTime>2026-11-07T23:59:59Z</endTime></timeSpan>", xml);
        Assert.Contains("<maxResults>50</maxResults><searchResultPostion>100</searchResultPostion>", xml);
        Assert.Contains("<metadataDescriptor>//recordType.meta.std-cgi.com</metadataDescriptor>", xml);
        Assert.NotNull(XDocument.Parse(xml));
    }

    [Fact]
    public void Download_request_escapes_the_uri_in_the_isapi_namespace()
    {
        var xml = HikXml.BuildDownloadRequest("rtsp://192.168.1.64/Streaming/tracks/101/?starttime=20261107T100000Z&endtime=20261107T101000Z");
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>", xml);
        Assert.Contains("<downloadRequest version=\"1.0\" xmlns=\"http://www.isapi.org/ver20/XMLSchema\">", xml);
        Assert.Contains("starttime=20261107T100000Z&amp;endtime=20261107T101000Z</playbackURI>", xml);
    }
}
