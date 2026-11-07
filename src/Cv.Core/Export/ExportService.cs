using Cv.Core.Nvr;

namespace Cv.Core.Export;

/// <summary>The export queue (Track A implements). clientForNvr returns the live client of a recorder id, or null when unknown.</summary>
public sealed class ExportService(Func<string, INvrClient?> clientForNvr, IMediaTools media, string tempFolder, TimeProvider? time = null)
    : IExportService, IDisposable
{
    public IReadOnlyList<ExportJob> Jobs => throw new NotImplementedException("Track A: ExportService");
    public event Action<ExportJob?>? JobChanged { add { } remove { } }
    public ExportJob Enqueue(ExportRequest request) => throw new NotImplementedException("Track A: ExportService");
    public void Cancel(Guid jobId) => throw new NotImplementedException("Track A: ExportService");
    public void Retry(Guid jobId) => throw new NotImplementedException("Track A: ExportService");
    public void ClearFinished() => throw new NotImplementedException("Track A: ExportService");
    public void Dispose() { _ = (clientForNvr, media, tempFolder, time); }
}
