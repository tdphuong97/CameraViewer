using System.IO;
using System.Net.Http;
using Cv.App.Demo;
using Cv.Core;
using Cv.Core.Export;
using Cv.Core.Hik;
using Cv.Core.Nvr;
using Cv.Core.Settings;
using Cv.Core.Update;
using Cv.Core.Video;
using Cv.Diagnostics;
using Cv.Media;

namespace Cv.App;

/// <summary>
/// Everything the screens use, composed once at start (real or demo). Owns the one INvrClient of the configured
/// recorder and recreates it when the recorder settings change. UI thread only, except where noted.
/// </summary>
public sealed class Services : IDisposable
{
    public const string UpdatePlatform = "windows";

    readonly List<INvrClient> _retired = [];
    readonly HttpClient _http;
    IReadOnlyList<ChannelInfo>? _channels;
    DateTime _channelsAt;
    Task<IReadOnlyList<ChannelInfo>>? _channelsTask;

    Services(bool demo, ISettingsStore store, INvrClientFactory nvrs, IVideoSourceFactory video, IMediaTools media, HttpClient http, string feed)
    {
        Demo = demo;
        Store = store;
        Settings = store.Load();
        NvrFactory = nvrs;
        Video = video;
        Media = media;
        _http = http;
        Updater = new Updater(http, feed);
        Probe = new ProbeRunner(nvrs, video, media);
        Version = AppVersion.Current;
    }

    public bool Demo { get; }
    public ISettingsStore Store { get; }
    public AppSettings Settings { get; }
    public INvrClientFactory NvrFactory { get; }
    public IVideoSourceFactory Video { get; }
    public IMediaTools Media { get; }
    public IExportService Export { get; private set; } = null!;
    public Updater Updater { get; }
    public ProbeRunner Probe { get; }
    public string Version { get; }

    /// <summary>The client of the configured recorder; null until one is configured.</summary>
    public INvrClient? Nvr { get; private set; }

    public NvrConfig? NvrConfig => Settings.Nvrs.FirstOrDefault();

    public bool IsConfigured => NvrConfig is { Host.Length: > 0 };

    /// <summary>Raised on the UI thread after the recorder client was replaced (settings saved).</summary>
    public event Action? NvrChanged;

    /// <summary>Device clock minus PC clock, learned when channels load; playback uses the device's "now".</summary>
    public TimeSpan ClockOffset { get; private set; }

    public DateTime DeviceNow => DateTime.Now + ClockOffset;

    public static Services CreateReal()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CameraViewer/" + AppVersion.Current);
        var media = new FFmpegMediaTools();
        var s = new Services(false, new SettingsStore(AppPaths.SettingsFile), new HikNvrClientFactory(), new FFmpegVideoSourceFactory(),
            media, http, Updater.ResolveFeed());
        s.Export = new ExportService(s.ClientFor, media, AppPaths.Temp);
        s.Init();
        return s;
    }

    public static Services CreateDemo()
    {
        var http = new HttpClient(new DemoUpdateHandler()) { Timeout = TimeSpan.FromMinutes(2) };
        // The real store on its own file: the demo never touches the real settings.
        var store = new SettingsStore(Path.Combine(AppPaths.Data, "settings-demo.json"));
        var s = new Services(true, store, new DemoNvrClientFactory(), new DemoVideoSourceFactory(), new DemoMediaTools(),
            http, DemoUpdateHandler.Feed);
        if (s.Settings.Nvrs.Count == 0)
            s.Settings.Nvrs.Add(new NvrConfig { Id = "demo", Name = "Đầu ghi", Host = "192.168.1.64", User = "admin" });
        s.Export = new DemoExportService(s.Settings.ExportFolder, DateTime.Now);
        s.Init();
        return s;
    }

    void Init()
    {
        Export.JobChanged += _ => DisposeRetiredWhenIdle();
        CreateClient();
    }

    /// <summary>For the export queue (any thread): the live client of a recorder id.</summary>
    INvrClient? ClientFor(string nvrId)
    {
        var c = Nvr;
        return c is not null && c.Config.Id == nvrId ? c : null;
    }

    void CreateClient()
    {
        var cfg = NvrConfig;
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.Host))
        {
            Nvr = null;
            return;
        }
        try
        {
            Nvr = NvrFactory.Create(cfg, Store.GetPassword(cfg.Id) ?? "");
        }
        catch (Exception ex)
        {
            Log.Error("Recorder client not created", ex);
            Nvr = null;
        }
    }

    /// <summary>Recorder settings changed: new client, channel list reloaded by whoever listens.</summary>
    public void ReloadNvr()
    {
        var old = Nvr;
        CreateClient();
        _channels = null;
        _channelsTask = null;
        if (old is not null)
        {
            lock (_retired) _retired.Add(old);
            DisposeRetiredWhenIdle();
        }
        NvrChanged?.Invoke();
    }

    /// <summary>A replaced client may still serve a running export; it is disposed once no export runs.</summary>
    void DisposeRetiredWhenIdle()
    {
        INvrClient[] done;
        lock (_retired)
        {
            if (_retired.Count == 0) return;
            if (Export.Jobs.Any(j => j.Status is ExportStatus.Queued or ExportStatus.Downloading or ExportStatus.Converting)) return;
            done = [.. _retired];
            _retired.Clear();
        }
        foreach (var c in done)
        {
            try { c.Dispose(); } catch (Exception ex) { Log.Warn("Old recorder client dispose failed", ex); }
        }
    }

    public IReadOnlyList<ChannelInfo>? Channels => _channels;

    /// <summary>True when the cached list is older than 5 minutes (online states change).</summary>
    public bool ChannelsStale => _channels is null || DateTime.UtcNow - _channelsAt > TimeSpan.FromMinutes(5);

    /// <summary>The channel list (cached until the recorder changes or <paramref name="refresh"/>); also learns the device clock.</summary>
    public Task<IReadOnlyList<ChannelInfo>> GetChannelsAsync(bool refresh = false)
    {
        if (!refresh && _channels is not null) return Task.FromResult(_channels);
        if (!refresh && _channelsTask is { IsCompleted: false }) return _channelsTask;
        return _channelsTask = LoadChannelsAsync();
    }

    async Task<IReadOnlyList<ChannelInfo>> LoadChannelsAsync()
    {
        var nvr = Nvr ?? throw new NvrException("Chưa cài đặt đầu ghi.");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var list = await nvr.GetChannelsAsync(cts.Token);
        if (ReferenceEquals(nvr, Nvr))
        {
            _channels = list;
            _channelsAt = DateTime.UtcNow;
        }
        _ = LearnClockAsync(nvr);
        return list;
    }

    async Task LearnClockAsync(INvrClient nvr)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var clock = await nvr.GetClockAsync(cts.Token);
            var offset = clock.LocalNow - DateTime.Now;
            // Ignore sub-second noise; a few seconds of drift is what we want to correct.
            ClockOffset = Math.Abs(offset.TotalSeconds) < 2 ? TimeSpan.Zero : offset;
        }
        catch (Exception ex)
        {
            Log.Warn("Device clock not read", ex);
        }
    }

    public void SaveSettings()
    {
        try
        {
            Store.Save(Settings);
        }
        catch (Exception ex)
        {
            Log.Error("Settings not saved", ex);
        }
    }

    /// <summary>
    /// The sound switch of the speaker button, shared by single view and Xem lại. Off at every start (a lobby camera must
    /// not speak when the window opens); once turned on it stays on while the app runs.
    /// </summary>
    public bool SoundOn { get; set; }

    /// <summary>
    /// A video source with the user's decoding choice. <paramref name="withAudio"/> only for single view and playback
    /// (the grid never takes sound); the source then follows <see cref="SoundOn"/> from the start.
    /// </summary>
    public IVideoSource CreateSource(string url, bool live, (int W, int H) target, bool withAudio = false)
    {
        var src = Video.Create(new VideoSourceOptions
        {
            Url = url,
            IsLive = live,
            HwDecode = Settings.HwDecode,
            TargetWidth = target.W,
            TargetHeight = target.H,
            Reconnect = live,
            WithAudio = withAudio,
        });
        if (withAudio && src is IAudioSource a) a.SoundOn = SoundOn;
        return src;
    }

    public void Dispose()
    {
        try { (Export as IDisposable)?.Dispose(); } catch (Exception ex) { Log.Warn("Export dispose failed", ex); }
        try { Nvr?.Dispose(); } catch (Exception ex) { Log.Warn("Recorder client dispose failed", ex); }
        lock (_retired)
        {
            foreach (var c in _retired)
                try { c.Dispose(); } catch (Exception) { }
            _retired.Clear();
        }
        try { (Video as IDisposable)?.Dispose(); } catch (Exception ex) { Log.Warn("Video factory dispose failed", ex); }
        _http.Dispose();
    }
}

public static class AppVersion
{
    /// <summary>"0.9.0" from the assembly's informational version, without the commit suffix.</summary>
    public static string Current
    {
        get
        {
            var v = typeof(AppVersion).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
            if (string.IsNullOrEmpty(v)) return "0.9.0";
            var plus = v.IndexOf('+');
            return plus > 0 ? v[..plus] : v;
        }
    }
}
