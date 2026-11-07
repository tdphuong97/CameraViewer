using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Cv.App.Logic;
using Cv.Core.Export;

namespace Cv.App.Saved;

/// <summary>One row of Video đã lưu, refreshed from its ExportJob on the UI thread.</summary>
public sealed class JobItem : INotifyPropertyChanged
{
    public JobItem(ExportJob job)
    {
        Job = job;
        var r = job.Request;
        CameraName = r.CameraName;
        DayText = TimeFormat.LongDate(r.From);
        RangeText = $"{TimeFormat.Clock(r.From)} – {TimeFormat.Clock(r.To)}  ·  {TimeFormat.Duration(r.To - r.From)}";
        Update();
    }

    public ExportJob Job { get; }
    public Guid Id => Job.Id;
    public string CameraName { get; }
    public string DayText { get; }
    public string RangeText { get; }

    public string StatusText { get; private set; } = "";
    public Brush StatusBrush { get; private set; } = Brushes.White;
    public string Glyph { get; private set; } = "";
    public Brush GlyphBrush { get; private set; } = Brushes.White;
    public Brush GlyphBackground { get; private set; } = Brushes.Transparent;
    public double Progress { get; private set; }
    public Visibility ProgressVisibility { get; private set; }
    public Visibility DoneVisibility { get; private set; }
    public Visibility CancelVisibility { get; private set; }
    public Visibility RetryVisibility { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    static Brush Soft(string hex) => (Brush)new BrushConverter().ConvertFromInvariantString(hex)!;

    static readonly Brush OkSoft = Freeze(Soft("#15301F"));
    static readonly Brush DangerSoft = Freeze(Soft("#341A1D"));
    static readonly Brush AccentSoft = Freeze(Soft("#1C2C47"));
    static readonly Brush MutedSoft = Freeze(Soft("#232733"));

    static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }

    public void Update()
    {
        var j = Job;
        var pct = (int)Math.Round(j.Progress * 100);
        (StatusText, StatusBrush, Glyph, GlyphBrush, GlyphBackground) = j.Status switch
        {
            ExportStatus.Queued => ("Đang chờ lượt…", Res("MutedBrush"), Glyphs.Clock, Res("MutedBrush"), MutedSoft),
            ExportStatus.Downloading => ($"Đang tải từ đầu ghi… {pct}%", Res("SubtleBrush"), Glyphs.Download, Res("AccentHoverBrush"), AccentSoft),
            ExportStatus.Converting => ($"Đang hoàn tất video… {pct}%", Res("SubtleBrush"), Glyphs.Sync, Res("AccentHoverBrush"), AccentSoft),
            ExportStatus.Done => ("Đã lưu" + SizeSuffix(j), Res("OkBrush"), Glyphs.Check, Res("OkBrush"), OkSoft),
            ExportStatus.Failed => (j.Error ?? "Không lưu được video.", Res("DangerBrush"), Glyphs.Error, Res("DangerBrush"), DangerSoft),
            _ => ("Đã hủy", Res("MutedBrush"), Glyphs.Cancel, Res("MutedBrush"), MutedSoft),
        };
        var running = j.Status is ExportStatus.Queued or ExportStatus.Downloading or ExportStatus.Converting;
        Progress = j.Progress;
        ProgressVisibility = j.Status is ExportStatus.Downloading or ExportStatus.Converting ? Visibility.Visible : Visibility.Collapsed;
        DoneVisibility = j.Status == ExportStatus.Done ? Visibility.Visible : Visibility.Collapsed;
        CancelVisibility = running ? Visibility.Visible : Visibility.Collapsed;
        RetryVisibility = j.Status is ExportStatus.Failed or ExportStatus.Canceled ? Visibility.Visible : Visibility.Collapsed;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    static string SizeSuffix(ExportJob j)
    {
        long size = 0;
        try
        {
            if (j.OutputPath is { } p && File.Exists(p)) size = new FileInfo(p).Length;
        }
        catch (Exception)
        {
        }
        if (size == 0) size = j.BytesDownloaded;
        return size > 0 ? " · " + TimeFormat.Size(size) : "";
    }
}
