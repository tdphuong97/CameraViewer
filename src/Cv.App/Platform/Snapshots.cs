using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cv.Core;
using Cv.Core.Video;

namespace Cv.App.Platform;

/// <summary>Saves the current frame of a source as a full-resolution JPG.</summary>
internal static class Snapshots
{
    /// <summary>Returns the saved path. Throws with a Vietnamese message when there is no picture or the folder is not writable.</summary>
    public static async Task<string> SaveAsync(IVideoSource source, string folder, string cameraName, DateTime when)
    {
        FrameSnapshot snap;
        try
        {
            snap = await source.CaptureAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Warn("Snapshot capture failed", ex);
            throw new InvalidOperationException("Chưa có hình để chụp. Hãy đợi camera hiện hình rồi thử lại.", ex);
        }

        return await Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(folder);
                var path = Shell.UniquePath(folder, $"{cameraName}_{when.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture)}", ".jpg");
                var bmp = BitmapSource.Create(snap.Width, snap.Height, 96, 96, PixelFormats.Bgr32, null, snap.Bgra, snap.Stride);
                var enc = new JpegBitmapEncoder { QualityLevel = 92 };
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using (var fs = File.Create(path)) enc.Save(fs);
                return path;
            }
            catch (Exception ex)
            {
                Log.Warn("Snapshot save failed", ex);
                throw new InvalidOperationException("Không lưu được ảnh vào thư mục đã chọn. Hãy đổi thư mục lưu ảnh trong Cài đặt.", ex);
            }
        });
    }
}
