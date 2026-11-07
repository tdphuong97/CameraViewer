namespace Cv.Core;

/// <summary>IProgress that calls back on the reporting thread (Progress&lt;T&gt; would post to a captured UI context).</summary>
internal sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
