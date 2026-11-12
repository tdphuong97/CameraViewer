using System.Windows.Threading;
using Cv.App.Video;
using Cv.Core;
using Cv.Core.Nvr;
using Cv.Core.Video;

namespace Cv.App.Playback;

public enum PlaybackStatus { Idle, Searching, Loading, Playing, Paused, NoRecording, Ended, Error }

/// <summary>
/// Recorded playback of one camera on one day (spec §5): spans from RecordingTimeline.Merge; opens
/// PlaybackUrl(ch, t, spanEnd) from the span containing t (else the next one); on Ended continues with the next span;
/// position = open time + source.Position; a jump disposes the source and opens a new one.
/// </summary>
public sealed class PlaybackController
{
    readonly Services _s;
    readonly VideoView _view;
    readonly Dispatcher _ui;
    readonly DispatcherTimer _jumpTimer;
    readonly DispatcherTimer _endTimer;
    IVideoSource? _source;
    Action? _audioHandler;
    DateTime _openAt;
    TimeRange? _span;
    TimeRange _pendingSpan;
    CancellationTokenSource? _loadCts;
    INvrClient? _timeModeFor;

    public PlaybackController(Services s, VideoView view)
    {
        _s = s;
        _view = view;
        _ui = view.Dispatcher;
        _jumpTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
        _jumpTimer.Tick += (_, _) =>
        {
            _jumpTimer.Stop();
            Open(_pendingSpan.Start, _pendingSpan);
        };
        // The end of a span is reached by the clock, not by the stream: on a file the recorder is still writing it sends
        // up to the asked end and then goes quiet without closing (DS-7732NI-M4 V5.04.081, 2026-11-08), which would
        // freeze the picture for 10 s and then read as a lost link.
        _endTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _endTimer.Tick += (_, _) =>
        {
            if (Status != PlaybackStatus.Playing || _source is not { State: VideoState.Playing } src || _span is not { } sp || !_view.HasFrame) return;
            if (_openAt + src.Position < sp.End - EndSlack) return;
            _endTimer.Stop();
            OnEnded();
        };
        _view.FirstFrame += _ => Raise();
    }

    /// <summary>How close to the span end counts as the end (a frame or two short of it).</summary>
    static readonly TimeSpan EndSlack = TimeSpan.FromMilliseconds(300);

    public ChannelInfo? Camera { get; private set; }
    public DateTime Day { get; private set; } = DateTime.Today;
    public IReadOnlyList<TimeRange> Spans { get; private set; } = [];
    public PlaybackStatus Status { get; private set; }
    public string? Message { get; private set; }
    public IVideoSource? Source => _source;
    public bool HasSpans => Spans.Count > 0;

    /// <summary>Raised on the UI thread when status, spans or camera change.</summary>
    public event Action? Changed;

    /// <summary>Device-local time of the frame on screen.</summary>
    public DateTime Position
    {
        get
        {
            if (_source is { State: VideoState.Playing or VideoState.Paused or VideoState.Ended } s && _view.HasFrame)
            {
                var p = _openAt + s.Position;
                return _span is { } sp && p > sp.End ? sp.End : p;
            }
            return _openAt;
        }
    }

    /// <summary>Searches the day's recordings of the camera, then plays from <paramref name="at"/>.</summary>
    public async Task LoadAsync(ChannelInfo camera, DateTime day, DateTime at)
    {
        Stop();
        Camera = camera;
        Day = day.Date;
        Spans = [];
        _openAt = at;
        _span = null;
        Set(PlaybackStatus.Searching, "Đang tải…");
        var cts = _loadCts = new CancellationTokenSource();
        try
        {
            var nvr = _s.Nvr ?? throw new NvrException("Chưa cài đặt đầu ghi.");
            if (!ReferenceEquals(_timeModeFor, nvr))
            {
                // Playback URLs need the recorder's time mode; detect it once per client (LocalWithZ when unsure).
                try { await nvr.ResolveTimeModeAsync(cts.Token); } catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn("Time mode not resolved", ex); }
                _timeModeFor = nvr;
            }
            var segments = await nvr.SearchAsync(camera.No, Day, Day.AddDays(1), cts.Token);
            if (cts.IsCancellationRequested) return;
            Spans = Merge(segments);
            Jump(at);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            Log.Warn($"Recording search failed for channel {camera.No}", ex);
            Set(PlaybackStatus.Error, ex is NvrException ? ex.Message : "Không tải được danh sách ghi hình. Hãy thử lại.");
        }
    }

    /// <summary>Plays from t, or says there is nothing and moves to the next recorded span.</summary>
    public void Jump(DateTime t)
    {
        _jumpTimer.Stop();
        if (Camera is null) return;
        var span = SpanAtOrAfter(Spans, t);
        if (span is null)
        {
            CloseSource();
            _openAt = t;
            _span = null;
            Set(PlaybackStatus.NoRecording, Spans.Count == 0 ? "Ngày này không có ghi hình" : "Không có ghi hình lúc này");
            return;
        }
        if (span.Value.Start > t)
        {
            CloseSource();
            _openAt = t;
            _pendingSpan = span.Value;
            Set(PlaybackStatus.NoRecording, $"Không có ghi hình lúc này. Chuyển đến {span.Value.Start:HH:mm:ss}…");
            _jumpTimer.Start();
            return;
        }
        Open(t, span.Value);
    }

    void Open(DateTime t, TimeRange span)
    {
        CloseSource(keepPicture: true);
        _openAt = t;
        _span = span;
        try
        {
            var nvr = _s.Nvr ?? throw new NvrException("Chưa cài đặt đầu ghi.");
            var url = nvr.PlaybackUrl(Camera!.No, t, span.End, StreamKind.Main);
            var src = _s.CreateSource(url, live: false, _view.TargetPixels(), withAudio: true);
            src.StateChanged += OnSourceState;
            if (src is IAudioSource audio)
            {
                // The speaker button follows what the stream carries (sound track found, no speakers...).
                _audioHandler = () => _ui.BeginInvoke(() => { if (ReferenceEquals(src, _source)) Raise(); });
                audio.AudioChanged += _audioHandler;
            }
            _source = src;
            _view.Source = src;
            src.Start();
            _endTimer.Start();
            Set(PlaybackStatus.Loading, "Đang tải…");
        }
        catch (Exception ex)
        {
            Log.Error("Playback source not created", ex);
            CloseSource();
            Set(PlaybackStatus.Error, ex is NvrException ? ex.Message : "Không phát được đoạn ghi hình này.");
        }
    }

    void OnSourceState(IVideoSource src) => _ui.BeginInvoke(() =>
    {
        if (!ReferenceEquals(src, _source)) return;
        switch (src.State)
        {
            case VideoState.Playing:
                Set(PlaybackStatus.Playing, null);
                break;
            case VideoState.Paused:
                Set(PlaybackStatus.Paused, null);
                break;
            case VideoState.Ended:
                OnEnded();
                break;
            case VideoState.Failed when _span is { } sp && _view.HasFrame && _openAt + src.Position >= sp.End - TimeSpan.FromSeconds(3):
                // Went quiet right at the end of the span (the recorder does that on the file it is writing): the end.
                Log.Info($"Playback ch{Camera?.No}: stream went quiet at the span end ({sp.End:HH:mm:ss}); treated as its end");
                OnEnded();
                break;
            case VideoState.Failed:
                Set(PlaybackStatus.Error, src.Error ?? "Không phát được đoạn ghi hình này.");
                break;
            case VideoState.Connecting:
            case VideoState.Reconnecting:
                Set(PlaybackStatus.Loading, src.Error ?? "Đang tải…");
                break;
        }
    });

    void OnEnded()
    {
        var end = _span?.End ?? Position;
        var next = Spans.FirstOrDefault(s => s.Start >= end);
        if (next.End > next.Start)
        {
            Open(next.Start, next);
            return;
        }
        if (Day == _s.DeviceNow.Date)
        {
            _ = ContinueTodayAsync(end);
            return;
        }
        _openAt = end;
        Set(PlaybackStatus.Ended, "Đã hết ghi hình trong ngày này");
    }

    /// <summary>Today keeps recording: search again and continue if the recorder has more after <paramref name="end"/>.</summary>
    async Task ContinueTodayAsync(DateTime end)
    {
        try
        {
            var nvr = _s.Nvr;
            if (nvr is null || Camera is null) return;
            var segments = await nvr.SearchAsync(Camera.No, Day, Day.AddDays(1), CancellationToken.None);
            Spans = Merge(segments);
            var span = SpanAtOrAfter(Spans, end);
            if (span is { } s && s.End - (s.Start > end ? s.Start : end) > TimeSpan.FromSeconds(2))
            {
                Open(s.Start > end ? s.Start : end, s);
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Recording refresh failed", ex);
        }
        _openAt = end;
        Set(PlaybackStatus.Ended, "Đã xem đến thời điểm mới nhất");
    }

    public void TogglePause()
    {
        switch (Status)
        {
            case PlaybackStatus.Playing when _source is not null:
                _source.Pause();
                Set(PlaybackStatus.Paused, null);
                break;
            case PlaybackStatus.Paused when _source is not null:
                _source.Resume();
                Set(PlaybackStatus.Playing, null);
                break;
            case PlaybackStatus.Ended:
            case PlaybackStatus.Error:
            case PlaybackStatus.Paused:
                Jump(Position);
                break;
        }
    }

    public void Skip(TimeSpan delta)
    {
        var t = Position + delta;
        var first = Day;
        var last = Day.AddDays(1).AddSeconds(-1);
        Jump(t < first ? first : t > last ? last : t);
    }

    /// <summary>Screen left or window minimized: free the stream, remember where we were.</summary>
    public DateTime Suspend()
    {
        var at = Position;
        Stop();
        _openAt = at;
        _view.ClearPicture();
        return at;
    }

    /// <summary>The recorder changed: forget camera, day and spans.</summary>
    public void Reset()
    {
        Stop();
        Camera = null;
        Spans = [];
        _span = null;
        _timeModeFor = null;
        _view.ClearPicture();
        Set(PlaybackStatus.Idle, null);
    }

    public void Stop()
    {
        _loadCts?.Cancel();
        _loadCts = null;
        _jumpTimer.Stop();
        CloseSource();
    }

    void CloseSource(bool keepPicture = false)
    {
        _endTimer.Stop();
        var s = _source;
        if (s is null) return;
        s.StateChanged -= OnSourceState;
        if (s is IAudioSource audio && _audioHandler is not null) audio.AudioChanged -= _audioHandler;
        _audioHandler = null;
        _view.Source = null;
        _source = null;
        if (!keepPicture) _view.ClearPicture();
        try { s.Dispose(); } catch (Exception ex) { Log.Warn("Playback source dispose failed", ex); }
    }

    void Set(PlaybackStatus status, string? message)
    {
        Status = status;
        Message = message;
        Raise();
    }

    void Raise() => Changed?.Invoke();

    static IReadOnlyList<TimeRange> Merge(IReadOnlyList<RecordSegment> segments) => RecordingTimeline.Merge(segments);

    static TimeRange? SpanAtOrAfter(IReadOnlyList<TimeRange> spans, DateTime t) => RecordingTimeline.SpanAtOrAfter(spans, t);
}
