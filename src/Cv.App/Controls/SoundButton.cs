using System.Windows.Controls;
using Cv.App.Logic;
using Cv.Core.Video;

namespace Cv.App.Controls;

/// <summary>The speaker button of single view and Xem lại: one place for how it looks and what a click does.</summary>
internal static class SoundButton
{
    public static void Refresh(Button button, IVideoSource? source, bool soundOn)
    {
        var audio = source as IAudioSource;
        var connected = source?.State is VideoState.Playing or VideoState.Paused;
        var state = SoundButtonState.For(connected, audio?.HasAudio ?? false, soundOn, audio?.AudioError);
        Ui.SetGlyph(button, state.On ? Glyphs.Volume : Glyphs.Mute);
        button.Content = state.Label;
        button.IsEnabled = state.Enabled;
        button.ToolTip = state.Tip;
    }

    /// <summary>Flips the shared switch and applies it to the source on screen. The caller refreshes the button.</summary>
    public static void Toggle(Services s, IVideoSource? source)
    {
        s.SoundOn = !s.SoundOn;
        if (source is IAudioSource audio) audio.SoundOn = s.SoundOn;
    }
}
