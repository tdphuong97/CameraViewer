using System.IO;
using System.Windows;
using Cv.App.Playback;
using Cv.App.Video;
using Cv.Core;
using Cv.Core.Export;

namespace Cv.App.Demo;

/// <summary>
/// --demo --selftest: walks through every screen the way clicks would (off screen, no mouse), checks behaviour and the
/// RAM rules (which sources are open), writes PASS/FAIL lines to the log and exits with the number of failures.
/// </summary>
internal static class DemoSelfTest
{
    public static async Task RunAsync(MainWindow w)
    {
        var fails = 0;
        void Check(bool ok, string what)
        {
            Log.Info((ok ? "SELFTEST PASS " : "SELFTEST FAIL ") + what);
            if (!ok) fails++;
        }
        static Task Wait(double s) => Task.Delay(TimeSpan.FromSeconds(s));
        var s = w.Services;
        var savedSnapshots = s.Settings.SnapshotFolder;

        try
        {
            s.Settings.LiveGrid = 0;
            w.ShowLive();
            await Wait(2.5);
            var live = w.LivePage;
            var online = live.TestOrder.Count(c => c.Online);
            Check(live.TestOrder.Count == DemoData.CameraCount, $"live: {live.TestOrder.Count} cameras listed");
            Check(FramePump.Count == online, $"live: {FramePump.Count} sources open = online cameras on screen ({online}), offline never opened");

            live.TestEnterSingle(0);
            await Wait(1.5);
            Check(live.TestInSingle && FramePump.Count == 1, $"single view: grid closed, one source ({FramePump.Count})");
            Check(live.TestSingleHasFrame, "single view: picture shown");
            Check(live.HandleEscape() && !live.TestInSingle, "Esc: back to all cameras");
            await Wait(1.5);
            Check(FramePump.Count == online, $"grid reopened ({FramePump.Count})");

            live.TestLayout(4);
            await Wait(1);
            Check(FramePump.Count == 4 && live.TestPageCount == (DemoData.CameraCount + 3) / 4, $"layout 4: {FramePump.Count} sources, {live.TestPageCount} pages");
            live.TestNextPage();
            await Wait(1);
            Check(FramePump.Count == 3, $"page 2 (camera 5 off): {FramePump.Count} sources");
            live.TestLayout(0);
            await Wait(1);

            var first = live.TestOrder[0].No;
            live.TestSwap(0, 1);
            Check(live.TestOrder[1].No == first && s.Settings.LiveOrder.Count == DemoData.CameraCount, "swap: order changed and saved");
            live.TestSwap(0, 1);
            await Wait(1);
            Check(FramePump.Count == online, $"after swaps: {FramePump.Count} sources");

            var snapDir = Path.Combine(AppPaths.Data, "selftest-snapshots");
            s.Settings.SnapshotFolder = snapDir;
            if (Directory.Exists(snapDir)) Directory.Delete(snapDir, true);
            live.TestEnterSingle(0);
            await Wait(1.5);
            var camName = live.TestOrder[0].Name;
            await w.SnapshotAsync(w.LivePage.TestSingleSource, camName, DateTime.Now);
            Check(Directory.Exists(snapDir) && Directory.GetFiles(snapDir, "*.jpg").Length == 1, "snapshot: one JPG written");
            live.HandleEscape();

            // ---- Playback ----
            var cam = live.TestOrder.First(c => c.No == 2);
            var start = s.DeviceNow.AddMinutes(-10);
            w.ShowPlayback(cam, start);
            await Wait(3);
            var pb = w.PlaybackPage;
            var ctrl = pb.TestController;
            Check(FramePump.Count == 1, $"playback: live closed, one source ({FramePump.Count})");
            Check(ctrl.Status == PlaybackStatus.Playing && ctrl.HasSpans, $"playback: playing ({ctrl.Status}), spans {ctrl.Spans.Count}");
            var p1 = ctrl.Position;
            await Wait(2);
            var p2 = ctrl.Position;
            Check(p2 - p1 > TimeSpan.FromSeconds(1.5) && Math.Abs((p1 - start).TotalSeconds) < 5, $"position runs: {p1:HH:mm:ss} → {p2:HH:mm:ss}");

            pb.TestTogglePause();
            await Wait(0.3);
            var p3 = ctrl.Position;
            await Wait(1.5);
            Check(ctrl.Status == PlaybackStatus.Paused && ctrl.Position - p3 < TimeSpan.FromSeconds(0.3), "pause holds position");
            pb.TestTogglePause();
            await Wait(1);
            Check(ctrl.Status == PlaybackStatus.Playing && ctrl.Position > p3, "resume");

            var before = ctrl.Position;
            ctrl.Skip(TimeSpan.FromSeconds(30));
            await Wait(1.5);
            var jumped = (ctrl.Position - before).TotalSeconds;
            Check(jumped is > 29 and < 34, $"+30 s jumps ({jumped:0.0} s)");

            var day = ctrl.Day;
            ctrl.Jump(day.AddHours(2).AddMinutes(10));
            await Wait(0.5);
            Check(ctrl.Status == PlaybackStatus.NoRecording, $"jump into the 02:00–02:30 gap: \"{ctrl.Message}\"");
            await Wait(2.5);
            Check(ctrl.Status == PlaybackStatus.Playing && ctrl.Position >= day.AddHours(2.5), $"then plays from next span ({ctrl.Position:HH:mm:ss})");

            ctrl.Jump(day.AddHours(2).AddSeconds(-2));
            await Wait(4.5);
            Check(ctrl.Position >= day.AddHours(2.5) && ctrl.Status == PlaybackStatus.Playing,
                $"end of span continues with the next one ({ctrl.Position:HH:mm:ss})");

            pb.TestMarkStart();
            await Wait(2.2);
            pb.TestMarkEnd();
            var (rs, re) = pb.TestRange;
            Check(rs is not null && re is not null && re > rs, $"marks: {rs:HH:mm:ss} → {re:HH:mm:ss}");

            var jobsBefore = s.Export.Jobs.Count;
            pb.OpenExportDialog();
            var dialog = w.ModalContent.Content as ExportDialog;
            Check(dialog is not null, "export dialog opens");
            dialog?.TestSave();
            Check(s.Export.Jobs.Count == jobsBefore + 1 && w.ModalHost.Visibility != Visibility.Visible, "Lưu: job queued, dialog closed");
            var job = s.Export.Jobs.OrderBy(j => j.CreatedAt).Last();
            Check(job.Request.Channel == 2 && job.Request.From == rs && job.Request.To == re, "job carries camera and marked range");

            w.WindowState = WindowState.Minimized;
            await Wait(0.5);
            Check(FramePump.Count == 0, $"minimized: every source closed ({FramePump.Count})");
            var resumeAt = ctrl.Position;
            w.WindowState = WindowState.Normal;
            await Wait(2);
            Check(FramePump.Count == 1 && ctrl.Status == PlaybackStatus.Playing && ctrl.Position >= resumeAt,
                $"restored: playback resumes ({ctrl.Status}, {ctrl.Position:HH:mm:ss})");

            w.ShowSettings();
            await Wait(0.5);
            Check(FramePump.Count == 0, "settings: no source open");
            w.ShowSaved();
            for (var i = 0; i < 90 && job.Status != ExportStatus.Done; i++) await Wait(0.5);
            Check(job.Status == ExportStatus.Done, $"export finishes ({job.Status})");
            await Wait(0.5);
            Check(w.Toasts.Children.Count > 0, "a toast tells the user");
            w.ShowLive();
            await Wait(1.5);
            Check(FramePump.Count == online, $"back to live: {FramePump.Count} sources");
        }
        catch (Exception ex)
        {
            Log.Error("SELFTEST crashed", ex);
            fails++;
        }
        finally
        {
            s.Settings.SnapshotFolder = savedSnapshots;
            s.SaveSettings();
        }
        Log.Info($"SELFTEST DONE failures={fails}");
        w.Close();
        Application.Current.Shutdown(fails);
    }
}
