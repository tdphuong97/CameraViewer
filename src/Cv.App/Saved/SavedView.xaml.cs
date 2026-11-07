using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Cv.App.Platform;
using Cv.Core;
using Cv.Core.Export;

namespace Cv.App.Saved;

/// <summary>Video đã lưu: export jobs, newest first, with progress and the actions each state allows.</summary>
public partial class SavedView : UserControl, IPage
{
    readonly ObservableCollection<JobItem> _items = [];
    IShell _shell = null!;
    Services _s = null!;

    public SavedView()
    {
        InitializeComponent();
        JobList.ItemsSource = _items;
    }

    public void Init(IShell shell)
    {
        _shell = shell;
        _s = shell.Services;
    }

    public void Activate()
    {
        FolderText.Text = "Thư mục: " + _s.Settings.ExportFolder;
        Refresh(Jobs());
    }

    public void Deactivate() { }

    IReadOnlyList<ExportJob> Jobs()
    {
        try
        {
            return [.. _s.Export.Jobs];
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotImplementedException)
        {
            return [];
        }
    }

    /// <summary>Syncs the rows with the service's jobs (called by the window after JobChanged, on the UI thread).</summary>
    public void Refresh(IReadOnlyList<ExportJob> jobs)
    {
        var ordered = jobs.OrderByDescending(j => j.CreatedAt).ToList();
        var byId = _items.ToDictionary(i => i.Id);
        for (var i = _items.Count - 1; i >= 0; i--)
            if (!ordered.Any(j => j.Id == _items[i].Id)) _items.RemoveAt(i);
        for (var i = 0; i < ordered.Count; i++)
        {
            var job = ordered[i];
            if (byId.TryGetValue(job.Id, out var item))
            {
                item.Update();
                var at = _items.IndexOf(item);
                if (at != i) _items.Move(at, i);
            }
            else _items.Insert(i, new JobItem(job));
        }
        EmptyPanel.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.Visibility = _items.Any(i => i.Job.Status is ExportStatus.Done or ExportStatus.Failed or ExportStatus.Canceled)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    static JobItem Item(object sender) => (JobItem)((FrameworkElement)sender).DataContext;

    void OnOpenVideo(object sender, RoutedEventArgs e) => Report(Shell.OpenFile(Item(sender).Job.OutputPath));

    void OnShowInFolder(object sender, RoutedEventArgs e) =>
        Report(Shell.ShowInFolder(Item(sender).Job.OutputPath, Item(sender).Job.Request.OutputFolder));

    void OnCancel(object sender, RoutedEventArgs e) => Safe(() => _s.Export.Cancel(Item(sender).Id));
    void OnRetry(object sender, RoutedEventArgs e) => Safe(() => _s.Export.Retry(Item(sender).Id));
    void OnClear(object sender, RoutedEventArgs e) => Safe(_s.Export.ClearFinished);
    void OnOpenFolder(object sender, RoutedEventArgs e) => Report(Shell.OpenFolder(_s.Settings.ExportFolder));

    void Report(string? error)
    {
        if (error is not null) _shell.Toast(error, ToastKind.Warn);
    }

    void Safe(Action a)
    {
        try
        {
            a();
        }
        catch (Exception ex)
        {
            Log.Warn("Export action failed", ex);
            _shell.Toast("Chưa làm được việc này. Hãy thử lại.", ToastKind.Warn);
        }
    }
}
