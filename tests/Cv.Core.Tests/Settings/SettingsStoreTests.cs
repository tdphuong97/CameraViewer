using System.Text.Json.Nodes;
using Cv.Core.Nvr;
using Cv.Core.Settings;
using Cv.Core.Video;

namespace Cv.Core.Tests.Settings;

public class SettingsStoreTests : IDisposable
{
    private readonly TempDir _dir = new();
    private string FilePath => _dir.File("settings.json");

    public void Dispose() => _dir.Dispose();

    private static AppSettings Sample() => new()
    {
        Nvrs = [new NvrConfig { Id = "n1", Name = "Đầu ghi tầng 1", Host = "192.168.1.64", HttpPort = 8080, RtspPort = 8554, User = "admin", TimeMode = TimeMode.Utc }],
        ExportFolder = @"D:\Video",
        SnapshotFolder = @"D:\Anh",
        HwDecode = HwDecodeMode.Off,
        PauseWhenMinimized = false,
        CheckUpdates = false,
        LiveGrid = 16,
        LiveOrder = [new CameraRef("n1", 3), new CameraRef("n1", 1)],
        LiveHidden = [new CameraRef("n1", 2)],
        LivePresets = [new LiveArrangement { Name = "Trực đêm", Grid = 105, Order = [new CameraRef("n1", 1)], Hidden = [new CameraRef("n1", 3)] }],
        LivePreset = "Trực đêm",
        Window = new WindowBounds(10, 20, 1600, 900, true),
    };

    [Fact]
    public void A_missing_file_loads_defaults()
    {
        var s = new SettingsStore(FilePath).Load();
        Assert.Empty(s.Nvrs);
        Assert.Equal(AppSettings.DefaultExportFolder, s.ExportFolder);
        Assert.True(s.PauseWhenMinimized);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void Settings_round_trip_as_camel_case_json_with_enum_names()
    {
        new SettingsStore(FilePath).Save(Sample());
        var loaded = new SettingsStore(FilePath).Load();
        Assert.Equal(Sample().Nvrs, loaded.Nvrs);
        Assert.Equal(Sample().LiveOrder, loaded.LiveOrder);
        Assert.Equal(Sample().LiveHidden, loaded.LiveHidden);
        Assert.Equal("Trực đêm", loaded.LivePreset);
        var preset = Assert.Single(loaded.LivePresets);
        Assert.Equal(("Trực đêm", 105), (preset.Name, preset.Grid));
        Assert.Equal(Sample().LivePresets[0].Order, preset.Order);
        Assert.Equal(Sample().LivePresets[0].Hidden, preset.Hidden);
        Assert.Equal(Sample().Window, loaded.Window);
        Assert.Equal((@"D:\Video", @"D:\Anh", HwDecodeMode.Off, false, false, 16),
            (loaded.ExportFolder, loaded.SnapshotFolder, loaded.HwDecode, loaded.PauseWhenMinimized, loaded.CheckUpdates, loaded.LiveGrid));

        var text = File.ReadAllText(FilePath);
        var root = JsonNode.Parse(text)!.AsObject();
        Assert.Equal(1, (int)root["version"]!);
        Assert.Equal("Utc", (string)root["settings"]!["nvrs"]![0]!["timeMode"]!);
        Assert.Equal("Off", (string)root["settings"]!["hwDecode"]!);
        Assert.NotNull(root["secrets"]);
        Assert.Contains("\n  ", text); // indented
        Assert.Contains("Đầu ghi tầng 1", text);
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void Passwords_are_dpapi_protected_and_survive_a_new_store()
    {
        var store = new SettingsStore(FilePath);
        store.Save(Sample());
        store.SetPassword("n1", "Ab@1#c!");
        Assert.DoesNotContain("Ab@1#c!", File.ReadAllText(FilePath));
        var secret = (string)JsonNode.Parse(File.ReadAllText(FilePath))!["secrets"]!["n1"]!;
        Assert.NotEmpty(Convert.FromBase64String(secret));

        var other = new SettingsStore(FilePath);
        Assert.Equal("Ab@1#c!", other.GetPassword("n1"));
        Assert.Null(other.GetPassword("unknown"));
        Assert.Equal("Đầu ghi tầng 1", other.Load().Nvrs[0].Name); // SetPassword kept the settings
    }

    [Fact]
    public void Set_password_saves_at_once_even_before_any_save()
    {
        new SettingsStore(FilePath).SetPassword("n1", "pw");
        Assert.True(File.Exists(FilePath));
        Assert.Equal("pw", new SettingsStore(FilePath).GetPassword("n1"));
    }

    [Fact]
    public void Set_password_does_not_pick_up_unsaved_changes_of_the_loaded_object()
    {
        var store = new SettingsStore(FilePath);
        store.Save(Sample());
        var live = store.Load();
        live.LiveGrid = 4; // edited in a dialog, not saved
        store.SetPassword("n1", "pw");
        Assert.Equal(16, new SettingsStore(FilePath).Load().LiveGrid);
    }

    [Fact]
    public void A_corrupt_file_loads_defaults_and_is_kept_as_bad()
    {
        File.WriteAllText(FilePath, "{ \"version\": 1, \"settings\": { \"nvrs\": [ ");
        var store = new SettingsStore(FilePath);
        var s = store.Load();
        Assert.Empty(s.Nvrs);
        Assert.False(File.Exists(FilePath));
        Assert.Equal("{ \"version\": 1, \"settings\": { \"nvrs\": [ ", File.ReadAllText(FilePath + ".bad"));
        store.Save(Sample());
        Assert.Single(new SettingsStore(FilePath).Load().Nvrs);
    }

    [Theory]
    [InlineData("{\"version\":1,\"settings\":{\"nvrs\":\"oops\"}}")]
    [InlineData("[1,2,3]")]
    [InlineData("")]
    [InlineData("{\"version\":1}")]
    public void Wrongly_shaped_json_is_corrupt(string content)
    {
        File.WriteAllText(FilePath, content);
        Assert.Empty(new SettingsStore(FilePath).Load().Nvrs);
        Assert.True(File.Exists(FilePath + ".bad"));
    }

    [Fact]
    public void Nulls_and_blank_folders_become_defaults()
    {
        File.WriteAllText(FilePath, "{\"version\":1,\"settings\":{\"nvrs\":[{\"id\":\"a\",\"host\":null}],\"liveOrder\":null,\"exportFolder\":\"\"},\"secrets\":{\"a\":null}}");
        var s = new SettingsStore(FilePath).Load();
        Assert.Equal("", s.Nvrs[0].Host);
        Assert.Empty(s.LiveOrder);
        Assert.Equal(AppSettings.DefaultExportFolder, s.ExportFolder);
    }

    [Fact]
    public void Settings_without_the_live_arrangement_fields_load_with_the_default_one()
    {
        File.WriteAllText(FilePath, "{\"version\":1,\"settings\":{\"liveGrid\":9,\"liveOrder\":[{\"nvrId\":\"a\",\"channel\":2}]},\"secrets\":{}}");
        var s = new SettingsStore(FilePath).Load();
        Assert.Equal(9, s.LiveGrid);
        Assert.Single(s.LiveOrder);
        Assert.Empty(s.LiveHidden);
        Assert.Empty(s.LivePresets);
        Assert.Equal("", s.LivePreset);
    }

    [Fact]
    public void Broken_live_arrangements_are_cleaned_up()
    {
        File.WriteAllText(FilePath, """
            {"version":1,"settings":{
              "liveHidden":[null,{"nvrId":null,"channel":1},{"nvrId":"a","channel":4}],
              "livePresets":[null,{"name":null},{"name":"  "},{"name":"Mặc định","grid":4},
                             {"name":"Đêm ","grid":105,"order":null,"hidden":[null,{"nvrId":"a","channel":2}]},{"name":"đêm"}],
              "livePreset":"Không có"},"secrets":{}}
            """);
        var s = new SettingsStore(FilePath).Load();
        Assert.Equal([new CameraRef("a", 4)], s.LiveHidden);
        var p = Assert.Single(s.LivePresets); // null, blank, the reserved default name and the duplicate are dropped
        Assert.Equal(("Đêm", 105), (p.Name, p.Grid));
        Assert.Empty(p.Order);
        Assert.Equal([new CameraRef("a", 2)], p.Hidden);
        Assert.Equal("", s.LivePreset); // the active name no longer exists
    }

    [Fact]
    public void An_unreadable_secret_is_null()
    {
        File.WriteAllText(FilePath, "{\"version\":1,\"settings\":{},\"secrets\":{\"a\":\"bm90LWRwYXBp\",\"b\":\"%%%\"}}");
        var store = new SettingsStore(FilePath);
        Assert.Null(store.GetPassword("a"));
        Assert.Null(store.GetPassword("b"));
    }

    [Fact]
    public void Parallel_writers_leave_a_valid_file_with_every_password()
    {
        var store = new SettingsStore(FilePath);
        Parallel.For(0, 20, i =>
        {
            store.SetPassword("n" + i, "pw" + i);
            if (i % 5 == 0) store.Save(Sample());
        });
        var fresh = new SettingsStore(FilePath);
        Assert.Single(fresh.Load().Nvrs);
        for (var i = 0; i < 20; i++) Assert.Equal("pw" + i, fresh.GetPassword("n" + i));
    }
}
