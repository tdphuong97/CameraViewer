namespace Cv.Core.Settings;

/// <summary>JSON settings with DPAPI-protected passwords (Track A implements). SetPassword saves the file at once.</summary>
public sealed class SettingsStore(string filePath) : ISettingsStore
{
    public string FilePath { get; } = filePath;
    public AppSettings Load() => throw new NotImplementedException("Track A: SettingsStore");
    public void Save(AppSettings settings) => throw new NotImplementedException("Track A: SettingsStore");
    public string? GetPassword(string nvrId) => throw new NotImplementedException("Track A: SettingsStore");
    public void SetPassword(string nvrId, string password) => throw new NotImplementedException("Track A: SettingsStore");
}
