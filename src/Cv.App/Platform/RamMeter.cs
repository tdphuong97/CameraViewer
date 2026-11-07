using System.Diagnostics;
using System.Windows.Threading;

namespace Cv.App.Platform;

/// <summary>Private bytes of this process every 2 s, for the RAM chip in the top bar.</summary>
internal sealed class RamMeter : IDisposable
{
    readonly Process _self = Process.GetCurrentProcess();
    readonly DispatcherTimer _timer;

    public RamMeter(Action<long> update)
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) =>
        {
            _self.Refresh();
            update(_self.PrivateMemorySize64);
        };
        _timer.Start();
        update(_self.PrivateMemorySize64);
    }

    public void Dispose()
    {
        _timer.Stop();
        _self.Dispose();
    }
}
