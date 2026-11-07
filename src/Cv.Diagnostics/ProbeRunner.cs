using Cv.Core.Export;
using Cv.Core.Nvr;
using Cv.Core.Video;

namespace Cv.Diagnostics;

/// <summary>Result of a probe: the full text report, whether every check passed, and where it was saved.</summary>
public sealed record ProbeReport(string Text, bool Passed, string? SavedPath);

/// <summary>
/// On-site check of a real recorder (Track D implements): device info, clock and time mode, channels, search,
/// live and playback RTSP (a few frames each), a 10-second ISAPI download by each method, remux to MP4.
/// Read-only on the recorder. Saves "probe-report-yyyyMMdd-HHmmss.txt" (password never written) into reportFolder.
/// </summary>
public sealed class ProbeRunner(INvrClientFactory nvrs, IVideoSourceFactory video, IMediaTools media)
{
    public Task<ProbeReport> RunAsync(NvrConfig config, string password, string reportFolder, IProgress<string>? progress, CancellationToken ct)
    {
        _ = (nvrs, video, media);
        throw new NotImplementedException("Track D: ProbeRunner");
    }
}
