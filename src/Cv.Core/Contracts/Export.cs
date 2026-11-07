// FROZEN CONTRACT. Only the lead changes this file; tracks report needed changes instead of editing it.
namespace Cv.Core.Export;

/// <summary>A piece of a media file to copy. From/To are offsets on the part's own timeline (first video packet = 0); null = from the start / to the end.</summary>
public sealed record RemuxPart(string Path, TimeSpan? From = null, TimeSpan? To = null);

/// <summary>Media work done with FFmpeg (implemented in Cv.Media). Stream copy only: never re-encodes.</summary>
public interface IMediaTools
{
    /// <summary>
    /// Joins the parts, in order, into one MP4 at <paramref name="outputPath"/> (timestamps made continuous), each part
    /// trimmed to [From, To) at the nearest keyframe at or before From. Video H.264/H.265 (H.265 tagged hvc1);
    /// AAC/MP3 audio copied, other audio (G.711, G.726...) dropped. Input may be Hikvision PS (IMKH header), MP4, TS.
    /// Writes to outputPath + ".part" and renames when done. Progress 0..1.
    /// </summary>
    Task RemuxAsync(IReadOnlyList<RemuxPart> parts, string outputPath, IProgress<double>? progress, CancellationToken ct);

    /// <summary>Time from the first to the last video packet; null when the file cannot be read or has no video.</summary>
    Task<TimeSpan?> GetDurationAsync(string path, CancellationToken ct);

    /// <summary>
    /// Pulls a stream (an RTSP playback URL) and writes <paramref name="duration"/> of media time to an MP4 by stream
    /// copy (real-time speed: the fallback when ISAPI download does not work). Progress 0..1 by media time.
    /// </summary>
    Task RecordAsync(string url, TimeSpan duration, string outputPath, IProgress<double>? progress, CancellationToken ct);
}

public enum ExportStatus { Queued, Downloading, Converting, Done, Failed, Canceled }

/// <summary>From/To are device-local wall clock. CameraName is used in the file name.</summary>
public sealed record ExportRequest(string NvrId, int Channel, string CameraName, DateTime From, DateTime To, string OutputFolder);

/// <summary>A queued or finished export. Mutated only by the export service; the UI reads it after JobChanged.</summary>
public sealed class ExportJob
{
    public required Guid Id { get; init; }
    public required ExportRequest Request { get; init; }
    public required DateTime CreatedAt { get; init; }
    public ExportStatus Status { get; set; }
    /// <summary>0..1 over the whole job (download ≈ 0..0.9, conversion 0.9..1).</summary>
    public double Progress { get; set; }
    public long BytesDownloaded { get; set; }
    /// <summary>The MP4 written, once Done.</summary>
    public string? OutputPath { get; set; }
    /// <summary>Vietnamese, for the user, when Failed.</summary>
    public string? Error { get; set; }
    /// <summary>How the video was fetched (DownloadMethod name), for the log and the probe.</summary>
    public string? Method { get; set; }
}

/// <summary>
/// Export queue. One job at a time per recorder (recorders limit download sessions), jobs of different recorders in
/// parallel. File name: "{CameraName}_{yyyy-MM-dd}_{HH'h'mm}-{HH'h'mm}.mp4" (invalid chars replaced, " (2)" added when
/// the file exists). Temporary files go to AppPaths.Temp and are deleted when the job ends. Jobs are kept in memory only.
/// </summary>
public interface IExportService
{
    IReadOnlyList<ExportJob> Jobs { get; }

    /// <summary>Raised on a background thread after any change to a job (status, progress, error), and when jobs are removed (job = null).</summary>
    event Action<ExportJob?>? JobChanged;

    /// <summary>Validates (To &gt; From, at most 24 h, folder writable) and queues. Throws ArgumentException with a Vietnamese message.</summary>
    ExportJob Enqueue(ExportRequest request);

    void Cancel(Guid jobId);

    /// <summary>Re-queues a Failed or Canceled job.</summary>
    void Retry(Guid jobId);

    /// <summary>Removes Done, Failed and Canceled jobs from the list.</summary>
    void ClearFinished();
}
