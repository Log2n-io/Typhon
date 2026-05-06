using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Workbench.Tests.Fixtures;

/// <summary>
/// Tiny SSE frame parser — reads <c>event: &lt;type&gt;\ndata: &lt;json&gt;\n\n</c> tuples from a
/// <see cref="StreamReader"/>. Tests use this to verify the typed-event wire format added in #308.
/// Default <c>message</c> events (no <c>event:</c> line) surface with <see cref="SseFrame.EventType"/>
/// equal to <c>"message"</c> so legacy untyped streams work the same way.
/// </summary>
internal readonly record struct SseFrame(string EventType, string Data);

internal static class SseFrameReader
{
    /// <summary>
    /// Reads frames from <paramref name="reader"/> one at a time. Yields the next complete frame
    /// or <see langword="null"/> when the stream ends. Comment lines (<c>: ...</c>) and field lines
    /// other than <c>event:</c> / <c>data:</c> (e.g., <c>id:</c>, <c>retry:</c>) are ignored.
    /// </summary>
    public static async Task<SseFrame?> ReadFrameAsync(StreamReader reader, CancellationToken ct)
    {
        string eventType = "message";
        string data = null;
        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null)
            {
                return data == null ? null : new SseFrame(eventType, data);
            }

            if (line.Length == 0)
            {
                // Empty line terminates the current frame. Only return if we accumulated a payload —
                // a stream that opens with a comment + blank line shouldn't surface as an empty frame.
                if (data != null)
                {
                    return new SseFrame(eventType, data);
                }
                continue;
            }

            if (line.StartsWith(":", System.StringComparison.Ordinal))
            {
                continue; // comment / keepalive
            }

            const string EventPrefix = "event:";
            const string DataPrefix = "data:";
            if (line.StartsWith(EventPrefix, System.StringComparison.Ordinal))
            {
                eventType = line[EventPrefix.Length..].Trim();
            }
            else if (line.StartsWith(DataPrefix, System.StringComparison.Ordinal))
            {
                var chunk = line[DataPrefix.Length..].TrimStart();
                data = data == null ? chunk : data + "\n" + chunk;
            }
            // Other field types (id:, retry:) are silently skipped — not used by Workbench streams.
        }
    }
}
