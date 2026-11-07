using Cv.Core.Export;

namespace Cv.Media;

/// <summary>Remux / duration / record with the FFmpeg libraries in-process (Track B implements).</summary>
public sealed class FFmpegMediaTools : IMediaTools
{
    public Task RemuxAsync(IReadOnlyList<RemuxPart> parts, string outputPath, IProgress<double>? progress, CancellationToken ct) =>
        throw new NotImplementedException("Track B: RemuxAsync");
    public Task<TimeSpan?> GetDurationAsync(string path, CancellationToken ct) =>
        throw new NotImplementedException("Track B: GetDurationAsync");
    public Task RecordAsync(string url, TimeSpan duration, string outputPath, IProgress<double>? progress, CancellationToken ct) =>
        throw new NotImplementedException("Track B: RecordAsync");
}
