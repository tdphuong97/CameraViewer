using System.Globalization;
using System.Security;
using System.Text;

namespace MockNvr;

/// <summary>The XML bodies, shaped like DS-7732NI-M4 firmware V4.7x answers.</summary>
public static class IsapiXml
{
    public const string Ns = "http://www.hikvision.com/ver20/XMLSchema";
    public const string Decl = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n";

    private static string E(string? s) => SecurityElement.Escape(s ?? "");

    public static string DeviceInfo() => Decl + $"""
        <DeviceInfo version="2.0" xmlns="{Ns}">
        <deviceName>Network Video Recorder</deviceName>
        <deviceID>{MockDevice.DeviceId}</deviceID>
        <model>{MockDevice.Model}</model>
        <serialNumber>{MockDevice.Serial}</serialNumber>
        <macAddress>{MockDevice.Mac}</macAddress>
        <firmwareVersion>{MockDevice.Firmware}</firmwareVersion>
        <firmwareReleasedDate>{MockDevice.FirmwareDate}</firmwareReleasedDate>
        <encoderVersion>V5.0</encoderVersion>
        <encoderReleasedDate>build 231208</encoderReleasedDate>
        <deviceType>NVR</deviceType>
        <telecontrolID>255</telecontrolID>
        <supportBeep>true</supportBeep>
        <supportVideoLoss>false</supportVideoLoss>
        </DeviceInfo>
        """;

    public static string Time(MockClock clock) => Decl + $"""
        <Time version="2.0" xmlns="{Ns}">
        <timeMode>NTP</timeMode>
        <localTime>{clock.LocalNow.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)}{clock.OffsetText}</localTime>
        <timeZone>{clock.PosixZone}</timeZone>
        </Time>
        """;

    private static string SourceDescriptor(MockChannel c) => $"""
        <sourceInputPortDescriptor>
        <proxyProtocol>HIKVISION</proxyProtocol>
        <addressingFormatType>ipaddress</addressingFormatType>
        <ipAddress>{c.Ip}</ipAddress>
        <managePortNo>8000</managePortNo>
        <srcInputPort>1</srcInputPort>
        <userName>admin</userName>
        <streamType>auto</streamType>
        <model>{c.Model}</model>
        <deviceID></deviceID>
        </sourceInputPortDescriptor>
        """;

    public static string InputProxyChannels()
    {
        var sb = new StringBuilder(Decl).Append($"<InputProxyChannelList version=\"2.0\" xmlns=\"{Ns}\">\n");
        foreach (var c in MockDevice.Channels)
            sb.Append($"<InputProxyChannel version=\"2.0\" xmlns=\"{Ns}\">\n<id>{c.No}</id>\n<name>{E(c.Name)}</name>\n")
              .Append(SourceDescriptor(c)).Append("\n<enableAnr>false</enableAnr>\n</InputProxyChannel>\n");
        return sb.Append("</InputProxyChannelList>\n").ToString();
    }

    public static string InputProxyStatus()
    {
        var sb = new StringBuilder(Decl).Append($"<InputProxyChannelStatusList version=\"2.0\" xmlns=\"{Ns}\">\n");
        foreach (var c in MockDevice.Channels)
            sb.Append($"<InputProxyChannelStatus version=\"2.0\" xmlns=\"{Ns}\">\n<id>{c.No}</id>\n")
              .Append(SourceDescriptor(c))
              .Append($"\n<online>{(c.Online ? "true" : "false")}</online>\n")
              .Append($"<chanDetectResult>{(c.Online ? "connect" : "netUnreachable")}</chanDetectResult>\n</InputProxyChannelStatus>\n");
        return sb.Append("</InputProxyChannelStatusList>\n").ToString();
    }

    public static string StreamingChannels()
    {
        var sb = new StringBuilder(Decl).Append($"<StreamingChannelList version=\"2.0\" xmlns=\"{Ns}\">\n");
        foreach (var c in MockDevice.Channels)
        {
            sb.Append(StreamingChannel(c, 1, "H.265", 1280, 720, 1024));
            sb.Append(StreamingChannel(c, 2, "H.264", 640, 360, 384));
        }
        return sb.Append("</StreamingChannelList>\n").ToString();
    }

    private static string StreamingChannel(MockChannel c, int stream, string codec, int w, int h, int kbps) => $"""
        <StreamingChannel version="2.0" xmlns="{Ns}">
        <id>{c.No * 100 + stream}</id>
        <channelName>{E(c.Name)}</channelName>
        <enabled>true</enabled>
        <Transport>
        <maxPacketSize>1000</maxPacketSize>
        <ControlProtocolList><ControlProtocol><streamingTransport>RTSP</streamingTransport></ControlProtocol><ControlProtocol><streamingTransport>HTTP</streamingTransport></ControlProtocol></ControlProtocolList>
        <Unicast><enabled>true</enabled><rtpTransportType>RTP/TCP</rtpTransportType></Unicast>
        <Security><enabled>true</enabled><certificateType>digest</certificateType></Security>
        </Transport>
        <Video>
        <enabled>true</enabled>
        <videoInputChannelID>{c.No}</videoInputChannelID>
        <videoCodecType>{codec}</videoCodecType>
        <videoScanType>progressive</videoScanType>
        <videoResolutionWidth>{w}</videoResolutionWidth>
        <videoResolutionHeight>{h}</videoResolutionHeight>
        <videoQualityControlType>VBR</videoQualityControlType>
        <fixedQuality>60</fixedQuality>
        <vbrUpperCap>{kbps}</vbrUpperCap>
        <maxFrameRate>{MockDevice.Fps * 100}</maxFrameRate>
        <GovLength>{MockDevice.Fps * 2}</GovLength>
        </Video>
        <Audio><enabled>false</enabled><audioInputChannelID>{c.No}</audioInputChannelID><audioCompressionType>G.711ulaw</audioCompressionType></Audio>
        </StreamingChannel>

        """;

    public static string AdminAccesses(MockOptions o) => Decl + $"""
        <AdminAccessProtocolList version="2.0" xmlns="{Ns}">
        <AdminAccessProtocol><id>1</id><enabled>true</enabled><protocol>HTTP</protocol><portNo>{o.Port}</portNo></AdminAccessProtocol>
        <AdminAccessProtocol><id>2</id><enabled>false</enabled><protocol>HTTPS</protocol><portNo>443</portNo></AdminAccessProtocol>
        <AdminAccessProtocol><id>3</id><enabled>true</enabled><protocol>DEV_MANAGE</protocol><portNo>8000</portNo></AdminAccessProtocol>
        <AdminAccessProtocol><id>4</id><enabled>true</enabled><protocol>RTSP</protocol><portNo>{o.RtspPort}</portNo></AdminAccessProtocol>
        </AdminAccessProtocolList>
        """;

    public static string UserCheckOk() => Decl + $"""
        <userCheck version="2.0" xmlns="{Ns}">
        <statusValue>200</statusValue>
        <statusString>OK</statusString>
        <isDefaultPassword>false</isDefaultPassword>
        <isRiskPassword>false</isRiskPassword>
        <isActivated>true</isActivated>
        </userCheck>
        """;

    /// <summary>The 401 body after a wrong password; unlockTime is in seconds, retryLoginTime = attempts left.</summary>
    public static string UserCheckFailed(bool locked, int retriesLeft, int unlockSeconds) => Decl + $"""
        <userCheck version="2.0" xmlns="{Ns}">
        <statusValue>401</statusValue>
        <statusString>Unauthorized</statusString>
        <isDefaultPassword>false</isDefaultPassword>
        <isRiskPassword>false</isRiskPassword>
        <isActivated>true</isActivated>
        <lockStatus>{(locked ? "lock" : "unlock")}</lockStatus>
        <unlockTime>{(locked ? unlockSeconds : 0)}</unlockTime>
        <retryLoginTime>{(locked ? 0 : retriesLeft)}</retryLoginTime>
        </userCheck>
        """;

    /// <summary>The web server's own 401 page, sent with a plain challenge.</summary>
    public const string UnauthorizedHtml =
        "<!DOCTYPE html>\n<html><head><title>Document Error: Unauthorized</title></head>\n" +
        "<body><h2>Access Error: 401 -- Unauthorized</h2>\n<p>Authentication Error</p>\n</body>\n</html>\n";

    public static string ResponseStatus(string requestUrl, int statusCode, string statusString, string subStatusCode) => Decl + $"""
        <ResponseStatus version="2.0" xmlns="{Ns}">
        <requestURL>{E(requestUrl)}</requestURL>
        <statusCode>{statusCode}</statusCode>
        <statusString>{E(statusString)}</statusString>
        <subStatusCode>{E(subStatusCode)}</subStatusCode>
        </ResponseStatus>
        """;

    public static string SearchResult(string searchId, IReadOnlyList<Segment> page, int position, int total, HikTime t, string rtspHost, string codecType)
    {
        var id = searchId.StartsWith('{') ? searchId : "{" + searchId + "}";
        var status = page.Count == 0 ? "NO MATCHES" : position + page.Count < total ? "MORE" : "OK";
        var sb = new StringBuilder(Decl).Append($"<CMSearchResult version=\"2.0\" xmlns=\"{Ns}\">\n")
            .Append($"<searchID>{E(id)}</searchID>\n<responseStatus>true</responseStatus>\n")
            .Append($"<responseStatusStrg>{status}</responseStatusStrg>\n<numOfMatches>{page.Count}</numOfMatches>\n");
        if (page.Count > 0)
        {
            sb.Append("<matchList>\n");
            foreach (var s in page)
            {
                var track = s.Channel * 100 + 1;
                var uri = $"rtsp://{rtspHost}/Streaming/tracks/{track}/?starttime={t.FormatCompact(s.Start)}&endtime={t.FormatCompact(s.End)}&name={s.Name}&size={s.Size}";
                sb.Append("<searchMatchItem>\n<sourceID>{0000000000-0000-0000-0000-000000000000}</sourceID>\n")
                  .Append($"<trackID>{track}</trackID>\n")
                  .Append($"<timeSpan>\n<startTime>{t.FormatIso(s.Start)}</startTime>\n<endTime>{t.FormatIso(s.End)}</endTime>\n</timeSpan>\n")
                  .Append($"<mediaSegmentDescriptor>\n<contentType>video</contentType>\n<codecType>{codecType}</codecType>\n")
                  .Append($"<playbackURI>{E(uri)}</playbackURI>\n</mediaSegmentDescriptor>\n")
                  .Append("<metadataMatches>\n<metadataDescriptor>recordType.meta.hikvision.com/timing</metadataDescriptor>\n</metadataMatches>\n")
                  .Append("</searchMatchItem>\n");
            }
            sb.Append("</matchList>\n");
        }
        return sb.Append("</CMSearchResult>\n").ToString();
    }
}
