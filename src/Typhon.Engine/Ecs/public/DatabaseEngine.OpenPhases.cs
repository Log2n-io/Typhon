using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Diagnostics;

namespace Typhon.Engine;

/// <summary>
/// The open's time line (#1665): the archetype initialization and the crash recovery cut into consecutive phases, each with its time and the data-file pages
/// it read and wrote, logged when the open completes. A phase ends where the next begins, so the phases partition the open from the archetype initialization
/// on; a phase entered several times (once per archetype) sums.
/// </summary>
public partial class DatabaseEngine
{
    /// <summary>One phase of the last open: its time and data-file I/O, summed over every time it was entered.</summary>
    internal sealed class OpenPhaseStat
    {
        public string Name;
        public double Ms;
        public long PagesRead;
        public long PagesWritten;
        public int Entries;
    }

    /// <summary>The last open's phases, in the order each was first entered. Null before the first open.</summary>
    internal List<OpenPhaseStat> LastOpenPhases { get; private set; }

    private OpenPhaseStat _openPhase;
    private long _openPhaseStart;
    private long _openPhaseRead;
    private long _openPhaseWritten;

    /// <summary>Starts a new time line, dropping the previous open's.</summary>
    private void BeginOpenPhases(string first)
    {
        LastOpenPhases = [];
        _openPhase = null;
        MarkOpenPhase(first);
    }

    /// <summary>Ends the current phase and enters <paramref name="name"/>; null ends the time line. Opening thread only.</summary>
    internal void MarkOpenPhase(string name)
    {
        if (LastOpenPhases == null)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var read = MMF.DataFilePagesRead;
        var written = MMF.DataFilePagesWritten;
        if (_openPhase != null)
        {
            _openPhase.Ms += (now - _openPhaseStart) * 1000.0 / Stopwatch.Frequency;
            _openPhase.PagesRead += read - _openPhaseRead;
            _openPhase.PagesWritten += written - _openPhaseWritten;
        }

        _openPhase = null;
        if (name == null)
        {
            return;
        }

        foreach (var phase in LastOpenPhases)
        {
            if (phase.Name == name)
            {
                _openPhase = phase;
                break;
            }
        }

        if (_openPhase == null)
        {
            _openPhase = new OpenPhaseStat { Name = name };
            LastOpenPhases.Add(_openPhase);
        }

        _openPhase.Entries++;
        _openPhaseStart = now;
        _openPhaseRead = read;
        _openPhaseWritten = written;
    }

    /// <summary>Ends the time line and logs each phase: at Information after a crash recovery, where it explains a slow start, else at Debug.</summary>
    private void EndOpenPhases()
    {
        MarkOpenPhase(null);
        if (LastOpenPhases == null)
        {
            return;
        }

        var level = CrashRecoveryAtOpen ? LogLevel.Information : LogLevel.Debug;
        foreach (var phase in LastOpenPhases)
        {
            LogOpenPhase(level, phase.Name, phase.Ms, phase.PagesRead, phase.PagesWritten, phase.Entries);
        }
    }

    [LoggerMessage("Open phase {phase}: {ms:F0} ms, {pagesRead} data-file pages read, {pagesWritten} written ({entries} entries)")]
    private partial void LogOpenPhase(LogLevel level, string phase, double ms, long pagesRead, long pagesWritten, int entries);
}
