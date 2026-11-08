namespace Cv.Core.Hik;

/// <summary>
/// The end of a downloaded Hikvision PS file. Some firmware cuts a download by time at a byte count, so the file stops
/// inside the last PES packet; FFmpeg still hands that frame to the decoder and the video ends on a smeared picture.
/// </summary>
internal static class PsTail
{
    /// <summary>Only the end of the file is read: a frame is far smaller than this.</summary>
    internal const int Window = 4 * 1024 * 1024;

    /// <summary>
    /// When the file ends inside a packet, cuts it at the pack that starts the last video frame (the first video PES
    /// with a PTS). Returns the bytes removed: 0 when the file ends on a packet boundary, when its end cannot be read
    /// as PS, or when the cut would leave almost nothing.
    /// </summary>
    public static long DropIncompleteFrame(string path)
    {
        try
        {
            long length, windowStart;
            byte[] tail;
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                length = f.Length;
                windowStart = Math.Max(0, length - Window);
                tail = new byte[length - windowStart];
                f.Position = windowStart;
                f.ReadExactly(tail);
            }
            var cut = FindCut(tail);
            if (cut < 0 || windowStart + cut < MinKeep) return 0;
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
                f.SetLength(windowStart + cut);
            return length - (windowStart + cut);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Could not check the end of " + path, e);
            return 0;
        }
    }

    /// <summary>Never cut a file to less than this (the 40-byte header and a frame or two).</summary>
    private const int MinKeep = 4096;

    /// <summary>
    /// Walks the packets from the first pack header in <paramref name="d"/>. Returns where to cut (start of the pack
    /// holding the last frame start) when the data ends inside a packet, else -1.
    /// </summary>
    internal static int FindCut(ReadOnlySpan<byte> d)
    {
        ReadOnlySpan<byte> packCode = [0, 0, 1, 0xBA];
        var i = d.IndexOf(packCode);
        if (i < 0) return -1;
        int pack = -1, frame = -1;
        var n = d.Length;
        while (true)
        {
            if (i == n) return -1; // ends on a packet boundary
            if (i + 4 > n) break;
            if (d[i] != 0 || d[i + 1] != 0 || d[i + 2] != 1)
            {
                // Lost the packet chain (data that only looked like a pack header): find the next one, forget the rest.
                var next = d[(i + 1)..].IndexOf(packCode);
                if (next < 0) return -1;
                i += 1 + next;
                pack = frame = -1;
                continue;
            }
            var code = d[i + 3];
            int size;
            if (code == 0xBA)
            {
                if (i + 14 > n) break;
                if ((d[i + 4] & 0xC0) != 0x40) { i++; continue; } // not an MPEG-2 pack header
                size = 14 + (d[i + 13] & 7);
                pack = i;
            }
            else if (code == 0xB9)
            {
                return -1; // program end code: a finished file
            }
            else if (code >= 0xBB)
            {
                if (i + 6 > n) break;
                size = 6 + ((d[i + 4] << 8) | d[i + 5]);
                // A video PES carrying a PTS starts a frame.
                if (code is >= 0xE0 and <= 0xEF && i + 8 <= n && (d[i + 7] & 0x80) != 0) frame = pack >= 0 ? pack : i;
            }
            else
            {
                i++;
                continue;
            }
            if (i + size > n) break;
            i += size;
        }
        return frame > 0 ? frame : -1;
    }
}
