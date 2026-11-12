using System.IO;
using System.Windows;
using Cv.App.Help;
using Cv.App.Logic;
using Cv.App.Playback;
using Cv.App.Video;
using Cv.Core;
using Cv.Core.Export;
using Cv.Core.Video;

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

            await LiveArrangementsAsync(w, live, Check);
            Check(FramePump.Count == online, $"back to automatic with nothing hidden: {FramePump.Count} sources");

            var sounds = await SoundAsync(w, live, Check);

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

            // Sound in Xem lại: the speaker button follows the shared switch (left on by the single-view checks).
            Check(pb.TestSoundEnabled && pb.TestSoundText == "Tắt tiếng", $"playback: speaker button \"{pb.TestSoundText}\" (switch carried over from single view)");
            Check(sounds.Sinks.Count > 0 && sounds.Sinks[^1].Samples > 0 && !sounds.Sinks[^1].Disposed, "playback: sound reaches the (silent) output");
            pb.TestToggleSound();
            Check(pb.TestSoundText == "Bật tiếng" && !s.SoundOn, "playback: speaker button switches the sound off");

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

            // Lưu video: the panel opens prefilled 2 min before to 3 min after the moment on screen.
            var at = ctrl.Position;
            pb.OpenSavePanel();
            var panel = pb.TestPanel;
            var pre = panel.TestRange;
            Check(pb.SavePanelOpen && pre is { } p0 && p0.From <= at && p0.To > at && p0.To - p0.From <= TimeSpan.FromMinutes(5),
                $"Lưu video: panel open, prefilled {pre?.From:HH:mm:ss} → {pre?.To:HH:mm:ss} around {at:HH:mm:ss}");
            Check(pb.TestTimelineRange == (pre?.From, pre?.To), "range drawn on the timeline");
            Check(w.HelpPage() == "xem-lai/luu-doan-video", "F1 with the panel open: page about saving");

            panel.TestFromHere();
            await Wait(2.2);
            panel.TestToHere();
            var (rs, re) = panel.TestRange ?? default;
            Check(re > rs && (re - rs).TotalSeconds is >= 1 and <= 4, $"Giờ đang xem twice: {rs:HH:mm:ss} → {re:HH:mm:ss}");
            Check(pb.TestTimelineRange == (rs, re), "timeline follows the panel");

            var jobsBefore = s.Export.Jobs.Count;
            panel.TestSave();
            Check(s.Export.Jobs.Count == jobsBefore + 1 && !pb.SavePanelOpen && pb.TestTimelineRange == (null, null),
                "Lưu video: job queued, panel closed, range cleared");
            var job = s.Export.Jobs.OrderBy(j => j.CreatedAt).Last();
            Check(job.Request.Channel == 2 && job.Request.From == rs && job.Request.To == re, "job carries camera and chosen range");

            pb.OpenSavePanel();
            Check(pb.HandleEscape() && !pb.SavePanelOpen, "Esc closes the panel");

            // Có người: the recorder's people detections for this camera and day.
            await Wait(0.5);
            Check(pb.TestVisits.Count > 0 && pb.TestPeopleButton == $"Có người · {pb.TestVisits.Count}" && pb.TestMarks == pb.TestVisits.Count,
                $"people: \"{pb.TestPeopleButton}\", {pb.TestMarks} marks on the timeline");
            pb.OpenPeoplePanel();
            await Wait(1.5);
            var people = pb.TestPeople;
            Check(pb.PeoplePanelOpen && people.TestVisits.Count == pb.TestVisits.Count, $"Có người panel lists {people.TestVisits.Count} visits");
            Check(people.TestThumbs > 0, $"pictures shown ({people.TestThumbs} loaded)");
            Check(w.HelpPage() == "xem-lai/tim-nguoi", "F1 with the people panel open: page about it");
            var visit = people.TestVisits[0];
            people.TestPick(0);
            await Wait(2.5);
            Check(Math.Abs((ctrl.Position - visit.Start).TotalSeconds) < 8, $"click a visit: plays {ctrl.Position:HH:mm:ss} for a person at {visit.Start:HH:mm:ss}");
            pb.OpenSavePanel();
            Check(pb.SavePanelOpen && !pb.PeoplePanelOpen, "one panel at a time");
            pb.HandleEscape();

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
            var settings = w.SettingsPage;
            settings.TestConnection();
            await Wait(1.5);
            Check(settings.TestResultText.StartsWith("Đã kết nối: DS-7732NI-M4 · ", StringComparison.Ordinal), $"Kiểm tra kết nối: \"{settings.TestResultText}\"");
            var oldClient = s.Nvr;
            settings.TestSaveRecorder();
            Check(!ReferenceEquals(oldClient, s.Nvr) && s.Nvr is not null, "Lưu: recorder client recreated");
            Check(pb.TestController.Camera is null, "playback forgets the old recorder's camera");
            w.ShowSaved();
            for (var i = 0; i < 90 && job.Status != ExportStatus.Done; i++) await Wait(0.5);
            Check(job.Status == ExportStatus.Done, $"export finishes ({job.Status})");
            await Wait(0.5);
            Check(w.Toasts.Children.Count > 0, "a toast tells the user");
            w.ShowLive();
            await Wait(2);
            Check(FramePump.Count == online, $"back to live (list reloaded): {FramePump.Count} sources");

            await GuideWindowAsync(w, live, Check);

            await w.CheckUpdateAsync(manual: false);
            Check(w.UpdateBanner.Visibility == Visibility.Visible && w.UpdateText.Text.Contains(DemoUpdateHandler.Next, StringComparison.Ordinal), $"update banner: \"{w.UpdateText.Text}\"");
            w.TestUpdateClick();
            await Wait(1.5);
            Check(w.UpdateProgress.Visibility == Visibility.Visible, $"update downloading: \"{w.UpdateText.Text}\"");
            for (var i = 0; i < 40 && w.UpdateBanner.Visibility == Visibility.Visible; i++) await Wait(0.5);
            Check(w.UpdateBanner.Visibility != Visibility.Visible, "update downloaded and verified (demo does not install)");
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

    /// <summary>
    /// Layouts (also the big-tile ones), hiding, the camera panel (drag from the list, across pages, onto a hidden one) and the
    /// Bộ menu, driven through the same entry points the clicks use. Leaves the default arrangement as it found it.
    /// </summary>
    static async Task LiveArrangementsAsync(MainWindow w, Live.LiveView live, Action<bool, string> check)
    {
        static Task Wait(double s) => Task.Delay(TimeSpan.FromSeconds(s));
        var s = w.Services;
        var total = live.TestAll.Count;
        int OnlineOnPage1() => live.TestOrder.Take(live.TestTileCount).Count(c => c.Online);

        // Layouts: the picker's codes, the big tile, and automatic that does not change.
        live.TestLayoutPicker.TestChoose(105);
        await Wait(1);
        check(live.TestTileCount == 6 && s.Settings.LiveGrid == 105 && FramePump.Count == OnlineOnPage1(),
            $"layout 1+5: {live.TestTileCount} tiles, {FramePump.Count} sources, saved {s.Settings.LiveGrid}");
        foreach (var (code, tiles) in new[] { (107, 8), (112, 13), (8, 8), (12, 12), (6, 6), (9, 9) })
        {
            live.TestLayout(code);
            check(live.TestTileCount == tiles, $"layout {code}: {live.TestTileCount} tiles (want {tiles})");
        }
        live.TestLayout(0);
        await Wait(0.5);
        check(live.TestTileCount == GridLayout.AutoSize(total), $"automatic: {live.TestTileCount} tiles for {total} cameras");

        // Hide: the camera leaves the grid and the count, stays in the list, and is saved.
        var hiddenCam = live.TestOrder[1];
        live.TestHide(1);
        await Wait(0.5);
        check(live.TestHiddenCount == 1 && live.TestOrder.All(c => c.No != hiddenCam.No) && live.TestAll.Count == total
              && s.Settings.LiveHidden.Count == 1, $"hide: {live.TestOrder.Count} shown, {live.TestHiddenCount} hidden, saved {s.Settings.LiveHidden.Count}");
        check(FramePump.Count == live.TestOrder.Take(live.TestTileCount).Count(c => c.Online), $"hide: sources match the tiles ({FramePump.Count})");

        // Camera panel: lists every camera, a hidden one dragged onto a tile shows and the camera it replaces moves to the end.
        live.TestSetDrawer(true);
        await Wait(0.5);
        check(live.TestDrawer.TestRows == total, $"camera panel: {live.TestDrawer.TestRows} rows");
        var replaced = live.TestOrder[2].No;
        live.TestDrop(2, hiddenCam.No);
        await Wait(0.5);
        check(live.TestOrder[2].No == hiddenCam.No && live.TestHiddenCount == 0 && live.TestOrder[^1].No == replaced && s.Settings.LiveHidden.Count == 0,
            $"drop a hidden camera on tile 3: shown, the replaced camera ({replaced}) is last");

        // Across pages: with 4 tiles the camera on page 2 goes onto tile 1 and swaps with its camera.
        live.TestLayout(4);
        await Wait(0.5);
        var far = live.TestOrder[5].No;
        var near = live.TestOrder[0].No;
        live.TestDrop(0, far);
        await Wait(0.5);
        check(live.TestOrder[0].No == far && live.TestOrder[5].No == near, "drop a camera from page 2: swapped with tile 1");
        live.TestDrop(0, far); // onto itself: nothing
        check(live.TestOrder[0].No == far, "drop onto the same camera: nothing changes");
        live.TestLayout(0);

        // The panel's Hiện lại button.
        live.TestHide(0);
        var gone = live.TestAll.First(c => !live.TestOrder.Contains(c)).No;
        live.TestDrawer.TestUnhide(gone);
        await Wait(0.5);
        check(live.TestHiddenCount == 0, "Hiện lại in the panel brings the camera back");

        // Bộ: a new one starts as a copy, edits stay inside it, switching restores each, delete goes back to the default.
        var menu = live.TestPresets;
        var defaultGrid = s.Settings.LiveGrid;
        check(menu.TestAdd("") != "", "Bộ: an empty name is refused");
        check(menu.TestAdd("Trực đêm") == "" && s.Settings.LivePreset == "Trực đêm" && s.Settings.LivePresets.Count == 1, "Bộ: \"Trực đêm\" added and in use");
        check(menu.TestAdd("trực đêm") != "", "Bộ: the same name in another case is refused");
        live.TestLayout(105);
        live.TestHide(0);
        live.TestHide(0);
        await Wait(0.5);
        check(s.Settings.LiveGrid == defaultGrid && s.Settings.LiveHidden.Count == 0 && s.Settings.LivePresets[0].Grid == 105 && s.Settings.LivePresets[0].Hidden.Count == 2,
            "Bộ: edits went into \"Trực đêm\", the default is untouched");
        menu.TestSwitch(LiveArrangements.DefaultName);
        await Wait(0.5);
        check(live.TestLayoutSetting == defaultGrid && live.TestHiddenCount == 0, $"Bộ: back to Mặc định (layout {live.TestLayoutSetting}, {live.TestHiddenCount} hidden)");
        menu.TestSwitch("Trực đêm");
        await Wait(0.5);
        check(live.TestLayoutSetting == 105 && live.TestHiddenCount == 2 && live.TestTileCount == 6, $"Bộ: \"Trực đêm\" again (layout {live.TestLayoutSetting}, {live.TestHiddenCount} hidden)");

        // Everything hidden: a message, the bottom bar stays so another Bộ can be picked.
        while (live.TestOrder.Count > 0) live.TestHide(0);
        await Wait(0.5);
        check(live.TestAllHiddenMessage && FramePump.Count == 0, $"every camera hidden: message shown, {FramePump.Count} sources");
        menu.TestDeleteActive();
        await Wait(1);
        check(s.Settings.LivePresets.Count == 0 && s.Settings.LivePreset == "" && !live.TestAllHiddenMessage && live.TestOrder.Count == total,
            "Bộ deleted: back to Mặc định with every camera");

        live.TestSetDrawer(false);
        live.TestLayout(defaultGrid);
        await Wait(1);
    }

    /// <summary>Output counters of the silent demo sound outputs, and the demo factory (to read which sources asked for sound).</summary>
    sealed record SoundRig(DemoVideoSourceFactory Factory, List<DemoSilentSink> Sinks);

    /// <summary>
    /// Sound in single view: the grid never asks for it; the speaker button is off until pressed, greyed for a camera with
    /// no sound track, tells when there are no speakers; the output stops when switched off. Leaves the switch ON so the
    /// Xem lại checks can see it carried over. Every output is silent.
    /// </summary>
    static async Task<SoundRig> SoundAsync(MainWindow w, Live.LiveView live, Action<bool, string> check)
    {
        static Task Wait(double s) => Task.Delay(TimeSpan.FromSeconds(s));
        var s = w.Services;
        var factory = (DemoVideoSourceFactory)s.Video;
        var sinks = new List<DemoSilentSink>();
        var failing = false;
        factory.AudioSinkFactory = () =>
        {
            var sink = new DemoSilentSink { Fail = failing };
            lock (sinks) sinks.Add(sink);
            return sink;
        };
        var rig = new SoundRig(factory, sinks);
        int WithAudio() { lock (factory.Created) return factory.Created.Count(c => c.WithAudio); }
        int GridWithAudio() { lock (factory.Created) return factory.Created.Count(c => c.WithAudio && c.Live && !c.Main); }
        s.SoundOn = false;

        // Earlier steps already opened single view (asks for sound, plays none while off); count from here.
        var before = WithAudio();
        int total;
        lock (factory.Created) total = factory.Created.Count;
        check(GridWithAudio() == 0, $"sound: the grid never asks for it ({total} sources created so far, {GridWithAudio()} grid tiles with sound)");

        live.TestEnterSingle(0);
        await Wait(1.5);
        check(WithAudio() == before + 1 && live.TestSoundEnabled && live.TestSoundText == "Bật tiếng", $"sound: single view asks for it, button \"{live.TestSoundText}\", off by default");
        check(sinks.Count == 0, "sound: nothing is opened while it is off");

        live.TestToggleSound();
        await Wait(1.2);
        check(live.TestSoundText == "Tắt tiếng" && s.SoundOn && sinks.Count == 1 && sinks[0].Samples > 2000, $"sound: switched on, {sinks.Count} output, {(sinks.Count > 0 ? sinks[0].Samples : 0)} samples");
        live.TestToggleSound();
        await Wait(0.4);
        var after = sinks[0].Samples;
        await Wait(0.6);
        check(live.TestSoundText == "Bật tiếng" && !s.SoundOn && sinks[0].Flushes >= 1 && sinks[0].Samples == after, "sound: switched off, output flushed and silent");

        // A camera with no sound track: greyed, with the plain message.
        live.HandleEscape();
        await Wait(1.2);
        var silent = live.TestOrder.ToList().FindIndex(c => c.No == DemoData.SilentChannel);
        live.TestEnterSingle(silent);
        await Wait(1.5);
        check(!live.TestSoundEnabled && live.TestSoundText == "Không có tiếng", $"sound: camera without a sound track: \"{live.TestSoundText}\", greyed");
        live.HandleEscape();
        await Wait(1.2);

        // No speakers: the picture goes on, the button tells why.
        failing = true;
        live.TestEnterSingle(0);
        await Wait(1.5);
        live.TestToggleSound();
        await Wait(1);
        var src = live.TestSingleSource as IAudioSource;
        check(src?.AudioError == "Máy không có loa hoặc tai nghe" && live.TestSoundEnabled && live.TestSingleHasFrame, $"sound: no speakers reported (\"{src?.AudioError}\"), picture still running");
        live.TestToggleSound(); // off: the message belongs to that attempt
        await Wait(0.8);
        check(src?.AudioError is null, "sound: the message clears when switched off");
        failing = false;
        live.TestToggleSound(); // on again, with a working output
        await Wait(1.2);
        check(live.TestSoundText == "Tắt tiếng" && sinks[^1].Samples > 0 && !sinks[^1].Fail, "sound: back on with a working output");
        live.HandleEscape();
        await Wait(1.5);
        await Wait(0.3);
        check(sinks.Count(x => !x.Disposed) == 0, $"sound: leaving single view closed every output ({sinks.Count} opened in all)");

        // The rule: only single view (live, main stream) and playback (recorded) ever take sound; no grid tile does.
        check(GridWithAudio() == 0 && WithAudio() >= before + 3, $"sound: {WithAudio()} sources with sound, none of them a grid tile");
        return rig;
    }

    /// <summary>F1 opens the guide in its own window on the page about the screen in use; one window is reused; closing
    /// it ends the WebView2 processes. Writes selftest-help.png (the guide as rendered) to the data folder.</summary>
    static async Task GuideWindowAsync(MainWindow w, Live.LiveView live, Action<bool, string> check)
    {
        static Task Wait(double s) => Task.Delay(TimeSpan.FromSeconds(s));
        if (!File.Exists(HelpWindow.FilePath))
        {
            check(false, "guide: help\\huong-dan.html shipped next to the exe");
            return;
        }
        if (!HelpWindow.RuntimeAvailable())
        {
            Log.Info("SELFTEST SKIP guide window: no WebView2 runtime on this PC");
            return;
        }

        w.OpenHelp();
        var help = HelpWindow.Current;
        if (help is null)
        {
            check(false, "guide: window opens");
            return;
        }
        var done = await Task.WhenAny(help.TestNavigated.Task, Wait(30));
        check(done == help.TestNavigated.Task && help.TestNavigated.Task.Result, "guide: opens in its own window (WebView2, offline file)");

        async Task<string> Title()
        {
            await Wait(0.8);
            var json = await help.TestScriptAsync("(document.querySelector('.page-head h1')||{}).textContent||''");
            return string.IsNullOrEmpty(json) ? "" : System.Text.Json.JsonSerializer.Deserialize<string>(json) ?? "";
        }
        var t = await Title();
        check(t == "Xem tất cả camera", $"guide on Trực tiếp: \"{t}\"");
        await help.TestCaptureAsync(Path.Combine(AppPaths.Data, "selftest-help.png"));

        live.TestEnterSingle(0);
        await Wait(1);
        w.OpenHelp();
        t = await Title();
        check(ReferenceEquals(HelpWindow.Current, help) && t == "Xem lớn một camera", $"guide on one camera, same window: \"{t}\"");
        live.HandleEscape();

        w.ShowPlaybackTab();
        w.OpenHelp();
        t = await Title();
        check(t == "Xem lại một camera", $"guide on Xem lại: \"{t}\"");

        w.ShowSaved();
        w.OpenHelp();
        t = await Title();
        check(t == "Mở video đã lưu", $"guide on Video đã lưu: \"{t}\"");

        w.ShowSettings();
        w.OpenHelp();
        t = await Title();
        check(t == "Các mục trong Cài đặt", $"guide on Cài đặt: \"{t}\"");

        // The pages only reached from the export dialog and the first start.
        foreach (var (page, title) in new[] { ("xem-lai/luu-doan-video", "Trích xuất video"), ("bat-dau/ket-noi-dau-ghi", "Kết nối đầu ghi lần đầu") })
        {
            HelpWindow.Open(w, page, () => { });
            t = await Title();
            check(t == title, $"guide page {page}: \"{t}\"");
        }

        var pid = help.TestBrowserProcessId;
        help.Close();
        var ended = false;
        for (var i = 0; i < 40 && !ended; i++)
        {
            await Wait(0.25);
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(pid);
                ended = p.HasExited;
            }
            catch (ArgumentException)
            {
                ended = true;
            }
        }
        check(HelpWindow.Current is null && pid != 0 && ended, $"guide closed: WebView2 processes ended (browser pid {pid})");

        w.ShowLive();
        await Wait(2);
    }
}
