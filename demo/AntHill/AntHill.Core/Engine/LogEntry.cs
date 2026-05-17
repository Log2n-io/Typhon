namespace AntHill.Core;

public enum LogSeverity : byte { Milestone, Tool, Depletion, Action }

/// <summary>
/// One line in the right-side event log. Produced by simulation systems (significant filter)
/// and tool actions; consumed by Godot's <c>EventLogHud</c>. Coordinates are in render metres
/// (0..100) so the click-to-fly handler can pass them straight to <c>SetFollowTarget</c>.
/// </summary>
public readonly struct LogEntry
{
    public readonly float TimeSec;
    public readonly string Text;
    public readonly float WorldX;     // render m
    public readonly float WorldZ;     // render m
    public readonly LogSeverity Severity;

    public LogEntry(float timeSec, string text, float worldX, float worldZ, LogSeverity severity)
    {
        TimeSec = timeSec;
        Text = text;
        WorldX = worldX;
        WorldZ = worldZ;
        Severity = severity;
    }
}
