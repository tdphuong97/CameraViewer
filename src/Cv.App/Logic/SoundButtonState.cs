namespace Cv.App.Logic;

/// <summary>What the speaker button of single view and Xem lại shows. On = the sound is switched on (loud icon).</summary>
public readonly record struct SoundButtonState(string Label, bool On, bool Enabled, string Tip)
{
    public const string NoSoundTip = "Camera này không có tiếng";

    /// <param name="connected">The picture is up (sound can only be judged once the stream has opened).</param>
    /// <param name="hasAudio">The stream has a sound track the app can play.</param>
    /// <param name="soundOn">The user's switch.</param>
    /// <param name="audioError">Why sound cannot play although the track is there (no speakers), or what is wrong with the track.</param>
    public static SoundButtonState For(bool connected, bool hasAudio, bool soundOn, string? audioError)
    {
        if (!connected) return new("Bật tiếng", false, false, "Chờ camera kết nối…");
        if (!hasAudio) return new("Không có tiếng", false, false, audioError ?? NoSoundTip);
        return soundOn
            ? new("Tắt tiếng", true, true, audioError ?? "Tắt tiếng của camera")
            : new("Bật tiếng", false, true, "Nghe tiếng của camera");
    }
}
