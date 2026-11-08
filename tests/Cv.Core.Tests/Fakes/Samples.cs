namespace Cv.Core.Tests.Fakes;

/// <summary>ISAPI answers exactly as in the design (§4), plus variants.</summary>
public static class Samples
{
    public const string DeviceInfo = """
        <DeviceInfo version="2.0" xmlns="http://www.hikvision.com/ver20/XMLSchema">
        <deviceName>Network Video Recorder</deviceName><deviceID>48443934-3537-3433-3732-c42f90e1a2b3</deviceID>
        <model>DS-7732NI-M4</model><serialNumber>DS-7732NI-M40820230815CCRRL12345678WCVU</serialNumber>
        <macAddress>c4:2f:90:e1:a2:b3</macAddress><firmwareVersion>V4.71.120</firmwareVersion>
        <firmwareReleasedDate>build 231208</firmwareReleasedDate><deviceType>NVR</deviceType></DeviceInfo>
        """;

    public const string DeviceInfoNoNs = """
        <?xml version="1.0" encoding="UTF-8"?>
        <DeviceInfo version="2.0"><deviceName>NVR</deviceName><model>DS-7732NI-M4</model>
        <serialNumber>SN1</serialNumber><firmwareVersion>V4.71.120</firmwareVersion></DeviceInfo>
        """;

    public const string Time = """
        <Time version="2.0" xmlns="http://www.hikvision.com/ver20/XMLSchema">
        <timeMode>NTP</timeMode><localTime>2026-11-07T19:45:12+07:00</localTime><timeZone>CST-7:00:00</timeZone></Time>
        """;

    public static string TimeAt(string localTime, string? timeZone = "CST-7:00:00") =>
        $"<Time version=\"2.0\" xmlns=\"http://www.hikvision.com/ver20/XMLSchema\"><timeMode>NTP</timeMode><localTime>{localTime}</localTime>"
        + (timeZone is null ? "" : $"<timeZone>{timeZone}</timeZone>") + "</Time>";

    /// <summary>Channels 3, 1, 2 (unsorted); channel 2 has no name.</summary>
    public const string Channels = """
        <InputProxyChannelList version="2.0" xmlns="http://www.hikvision.com/ver20/XMLSchema">
        <InputProxyChannel version="2.0"><id>3</id><name>Ham xe</name>
        <sourceInputPortDescriptor><proxyProtocol>HIKVISION</proxyProtocol><addressingFormatType>ipaddress</addressingFormatType>
        <ipAddress>192.168.254.4</ipAddress><managePortNo>8000</managePortNo><srcInputPort>1</srcInputPort>
        <userName>admin</userName><streamType>auto</streamType><model>DS-2CD1043G2-LIU</model></sourceInputPortDescriptor>
        <enableAnr>false</enableAnr></InputProxyChannel>
        <InputProxyChannel version="2.0"><id>1</id><name>Cong chinh</name>
        <sourceInputPortDescriptor><proxyProtocol>HIKVISION</proxyProtocol><addressingFormatType>ipaddress</addressingFormatType>
        <ipAddress>192.168.254.2</ipAddress><managePortNo>8000</managePortNo><srcInputPort>1</srcInputPort>
        <userName>admin</userName><streamType>auto</streamType><model>DS-2CD1043G2-LIU</model></sourceInputPortDescriptor>
        <enableAnr>false</enableAnr></InputProxyChannel>
        <InputProxyChannel version="2.0"><id>2</id><name></name>
        <sourceInputPortDescriptor><ipAddress>192.168.254.3</ipAddress></sourceInputPortDescriptor></InputProxyChannel>
        </InputProxyChannelList>
        """;

    public const string ChannelsNoNs = """
        <InputProxyChannelList><InputProxyChannel><id>1</id><name>Cong chinh</name>
        <sourceInputPortDescriptor><ipAddress>192.168.254.2</ipAddress><model>DS-2CD1043G2-LIU</model></sourceInputPortDescriptor>
        </InputProxyChannel></InputProxyChannelList>
        """;

    /// <summary>Channel 2 offline; channel 3 only has chanDetectResult.</summary>
    public const string Status = """
        <InputProxyChannelStatusList version="2.0" xmlns="http://www.hikvision.com/ver20/XMLSchema">
        <InputProxyChannelStatus version="2.0"><id>1</id><sourceInputPortDescriptor><ipAddress>192.168.254.2</ipAddress>
        </sourceInputPortDescriptor><online>true</online><chanDetectResult>connect</chanDetectResult></InputProxyChannelStatus>
        <InputProxyChannelStatus version="2.0"><id>2</id><sourceInputPortDescriptor><ipAddress>192.168.254.3</ipAddress>
        </sourceInputPortDescriptor><online>false</online><chanDetectResult>offline</chanDetectResult></InputProxyChannelStatus>
        <InputProxyChannelStatus version="2.0"><id>3</id><chanDetectResult>connect</chanDetectResult></InputProxyChannelStatus>
        </InputProxyChannelStatusList>
        """;

    public const string StreamingChannels = """
        <StreamingChannelList version="2.0" xmlns="http://www.hikvision.com/ver20/XMLSchema">
        <StreamingChannel><id>101</id><channelName>Camera 01</channelName><enabled>true</enabled></StreamingChannel>
        <StreamingChannel><id>102</id><channelName>Camera 01 sub</channelName></StreamingChannel>
        <StreamingChannel><id>201</id><channelName>Sanh tang 1</channelName></StreamingChannel>
        <StreamingChannel><id>301</id><channelName>Ham xe B1</channelName></StreamingChannel>
        </StreamingChannelList>
        """;

    public const string SearchPage = """
        <CMSearchResult version="2.0" xmlns="http://www.hikvision.com/ver20/XMLSchema">
        <searchID>{C9A2C7B1-6E2D-4F1A-9B0E-3D7C2A1F5E44}</searchID><responseStatus>true</responseStatus>
        <responseStatusStrg>MORE</responseStatusStrg><numOfMatches>50</numOfMatches>
        <matchList><searchMatchItem><sourceID>{0000000000-0000-0000-0000-000000000000}</sourceID><trackID>101</trackID>
        <timeSpan><startTime>2026-11-07T00:00:00Z</startTime><endTime>2026-11-07T01:02:11Z</endTime></timeSpan>
        <mediaSegmentDescriptor><contentType>video</contentType><codecType>H.265</codecType>
        <playbackURI>rtsp://192.168.1.64/Streaming/tracks/101/?starttime=20261107T000000Z&amp;endtime=20261107T010211Z&amp;name=00000000123000000&amp;size=524288000</playbackURI>
        </mediaSegmentDescriptor><metadataMatches><metadataDescriptor>recordType.meta.hikvision.com/timing</metadataDescriptor></metadataMatches>
        </searchMatchItem></matchList></CMSearchResult>
        """;

    public const string NotSupported = """
        <ResponseStatus version="2.0" xmlns="http://www.hikvision.com/ver20/XMLSchema"><requestURL>/ISAPI/ContentMgmt/download</requestURL>
        <statusCode>4</statusCode><statusString>Invalid Operation</statusString><subStatusCode>notSupport</subStatusCode></ResponseStatus>
        """;

    public const string Ok = """
        <ResponseStatus version="2.0" xmlns="http://www.hikvision.com/ver20/XMLSchema"><requestURL>/ISAPI/x</requestURL>
        <statusCode>1</statusCode><statusString>OK</statusString><subStatusCode>ok</subStatusCode></ResponseStatus>
        """;

    public const string WrongPassword =
        "<userCheck><statusValue>401</statusValue><statusString>Unauthorized</statusString><isActivated>true</isActivated>"
        + "<lockStatus>unlock</lockStatus><unlockTime>0</unlockTime><retryLoginTime>4</retryLoginTime></userCheck>";

    public const string Locked =
        "<userCheck version=\"2.0\" xmlns=\"http://www.hikvision.com/ver20/XMLSchema\"><statusValue>401</statusValue>"
        + "<statusString>Unauthorized</statusString><lockStatus>lock</lockStatus><unlockTime>1785</unlockTime>"
        + "<retryLoginTime>0</retryLoginTime></userCheck>";

    /// <summary>A Hikvision PS start: 40-byte IMKH header, then a pack header and some payload.</summary>
    public static byte[] Ps(int payload = 1000)
    {
        var b = new byte[40 + 4 + payload];
        "IMKH"u8.CopyTo(b);
        b[40] = 0; b[41] = 0; b[42] = 1; b[43] = 0xBA;
        for (var i = 44; i < b.Length; i++) b[i] = (byte)(i * 7);
        return b;
    }

    /// <summary>
    /// A well-formed Hikvision PS: IMKH header, then per frame a pack header + video PES with a PTS (1000 bytes) and a
    /// pack header + continuation PES without PTS (1000 bytes): 2051 bytes a frame. FrameStarts: offset of each frame's
    /// first pack header.
    /// </summary>
    public static (byte[] Data, List<int> FrameStarts) FramedPs(int frames)
    {
        var s = new MemoryStream();
        var header = new byte[40];
        "IMKH"u8.CopyTo(header);
        s.Write(header);
        byte[] pack = [0, 0, 1, 0xBA, 0x44, 0, 4, 0, 4, 1, 0x01, 0x89, 0xC3, 0xF8];
        var payload = Enumerable.Repeat((byte)0x55, 1000).ToArray();
        var starts = new List<int>();
        for (var f = 0; f < frames; f++)
        {
            starts.Add((int)s.Length);
            s.Write(pack);
            s.Write([0, 0, 1, 0xE0, 0x03, 0xF0, 0x80, 0x80, 0x05, 0x21, 0x00, 0x01, 0x00, 0x01]); // PES length 1008, PTS
            s.Write(payload);
            s.Write(pack);
            s.Write([0, 0, 1, 0xE0, 0x03, 0xEB, 0x80, 0x00, 0x00]); // PES length 1003, no PTS
            s.Write(payload);
        }
        return (s.ToArray(), starts);
    }

    /// <summary>
    /// The 24 bytes DS-7732NI-M4 firmware V5.04.081 sends before "IMKH" when downloading by time (seen on a real
    /// recorder, 2026-11-08).
    /// </summary>
    public static readonly byte[] TimeDownloadPrefix =
        [0, 0, 0, 0x40, 0, 0, 0, 0x13, 0, 0x0A, 0x3B, 0xC0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x28];
}
