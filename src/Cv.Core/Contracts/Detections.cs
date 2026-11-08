// FROZEN CONTRACT (2026-11-08). Only the lead changes this file; tracks report needed changes instead of editing it.
namespace Cv.Core.Nvr;

/// <summary>
/// One "person seen" the recorder logged: the camera's own human detection (Motion Detection 2.0 with target
/// "human"), stored by the NVR as a peopleDetection event with a full-frame picture. Time is the device wall clock
/// (Kind Unspecified), like every other time in the app. PictureUri is the path + query on the recorder
/// (e.g. "/picture/Streaming/tracks/1403/?name=...&amp;size=278614"), host removed; null when the event has no picture.
/// </summary>
public sealed record DetectionEvent(int Channel, DateTime Time, string? PictureUri);

/// <summary>A person on screen for a while: detections less than <see cref="PeopleTimeline.Gap"/> apart, merged.
/// PictureUri is the first detection's picture.</summary>
public sealed record PeopleVisit(int Channel, DateTime Start, DateTime End, string? PictureUri, int Count);

/// <summary>Thrown when the recorder has no people-detection search (old firmware, other brand).</summary>
public sealed class NvrDetectionUnsupportedException(string message, Exception? inner = null) : NvrException(message, inner);

/// <summary>
/// Recorder-side people detections. HikNvrClient implements it; callers test `client is INvrDetections`
/// (a separate interface so existing INvrClient fakes stay valid).
/// </summary>
public interface INvrDetections
{
    /// <summary>
    /// People detections of one channel with from &lt;= Time &lt; to (device local), oldest first; empty when none.
    /// Throws NvrDetectionUnsupportedException when the recorder cannot search them, NvrAuthException /
    /// NvrUnreachableException like the other calls.
    /// </summary>
    Task<IReadOnlyList<DetectionEvent>> SearchPeopleAsync(int channel, DateTime from, DateTime to, CancellationToken ct);

    /// <summary>The picture the recorder saved for a detection (JPEG bytes).</summary>
    Task<byte[]> GetPictureAsync(string pictureUri, CancellationToken ct);
}

/// <summary>Detections → visits.</summary>
public static class PeopleTimeline
{
    /// <summary>Detections closer than this belong to the same visit (the recorder logs about one a second while a
    /// person stays in view).</summary>
    public static readonly TimeSpan Gap = TimeSpan.FromSeconds(60);

    /// <summary>Merges detections (any order) into visits, oldest first, per channel.</summary>
    public static IReadOnlyList<PeopleVisit> Merge(IEnumerable<DetectionEvent> events)
    {
        var visits = new List<PeopleVisit>();
        foreach (var group in events.GroupBy(e => e.Channel))
        {
            PeopleVisit? current = null;
            foreach (var e in group.OrderBy(e => e.Time))
            {
                if (current is not null && e.Time - current.End < Gap)
                {
                    current = current with { End = e.Time, Count = current.Count + 1, PictureUri = current.PictureUri ?? e.PictureUri };
                    continue;
                }
                if (current is not null) visits.Add(current);
                current = new PeopleVisit(e.Channel, e.Time, e.Time, e.PictureUri, 1);
            }
            if (current is not null) visits.Add(current);
        }
        visits.Sort((a, b) => a.Start.CompareTo(b.Start));
        return visits;
    }
}
