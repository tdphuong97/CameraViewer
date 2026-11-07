using Cv.Core.Export;

namespace Cv.App.Demo;

/// <summary>Media tools of the demo: there is no real video to convert, so every call says so.</summary>
internal sealed class DemoMediaTools : IMediaTools
{
    const string NoVideo = "Bản thử không xử lý video thật.";

    public Task RemuxAsync(IReadOnlyList<RemuxPart> parts, string outputPath, IProgress<double>? progress, CancellationToken ct) =>
        Task.FromException(new NotSupportedException(NoVideo));

    public Task<TimeSpan?> GetDurationAsync(string path, CancellationToken ct) => Task.FromResult<TimeSpan?>(null);

    public Task RecordAsync(string url, TimeSpan duration, string outputPath, IProgress<double>? progress, CancellationToken ct) =>
        Task.FromException(new NotSupportedException(NoVideo));
}
