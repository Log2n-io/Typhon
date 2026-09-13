using System;
using System.Text;

namespace Typhon.Engine;

/// <summary>
/// Hang-watchdog diagnostic. Renders the live per-system scheduler state as a human-readable
/// table — predecessor count, ready flag, chunk progress, completion-skip reason. Consumers
/// (the bridge / app code) call <see cref="DumpHangDiagnostic"/> when they suspect the tick
/// loop is stuck so the engine state at the moment of the hang can be inspected without a
/// debugger attach.
/// </summary>
public sealed partial class DagScheduler
{
    // Per-system "most recent exception" capture. Populated by the system-execution catch
    // blocks via <see cref="CaptureSystemException"/>; surfaced in <see cref="DumpHangDiagnostic"/>
    // so a hang caused by a runaway exception gives the user the actual stack trace, not just a
    // "Skip=Exception" enum value. Allocated eagerly alongside the other per-system arrays in the constructor. It used to be lazy (`??=`) to spare schedulers
    // with no diagnostic need the per-system slot cost, but that races: several workers throwing in the same tick can each evaluate the `??=` and allocate,
    // and every capture but the winner's is silently dropped. An array of null references is 8 bytes per system — not a cost worth a race.
    private readonly Exception[] _lastSystemException;

    internal void CaptureSystemException(int sysIdx, Exception ex)
    {
        if ((uint)sysIdx < (uint)_lastSystemException.Length)
        {
            _lastSystemException[sysIdx] = ex;
        }
    }

    /// <summary>
    /// Returns a multi-line snapshot of the scheduler's current tick state, suitable for
    /// logging when a tick fails to complete. Cheap enough to call ad-hoc — O(systemCount)
    /// reads of internal counters.
    /// </summary>
    public string DumpHangDiagnostic()
    {
        var sb = new StringBuilder();
        sb.Append("DagScheduler hang diagnostic — tick ").Append(_currentTickNumber)
          .Append(", systemsRemaining=").Append(_systemsRemaining.Value)
          .Append(", tickInProgress=").Append(_tickInProgress)
          .AppendLine();

        sb.AppendLine();
        sb.AppendLine("Idx Phase                Name                            Pred  Deps  Ready  Claimed/Of  Left  Skip");
        sb.AppendLine("─── ──────────────────── ─────────────────────────────── ───── ───── ────── ────────── ───── ────");

        for (var i = 0; i < AllSystemCount; i++)
        {
            var sys = Systems[i];
            var deps = _remainingDeps[i].Value;
            var ready = _isReady[i].Value;
            var word = _claims[i].Value;
            var nextChunk = (uint)word;
            var totalChunks = (uint)(word >> 32);
            var skip = _currentTickSystemMetrics[i].SkipReason;
            var phase = sys.Phase.Name;

            // Claimed/Of is the live dispatch's claim word (0/0 while closed); Left is _remainingChunks, the chunks not yet counted down.
            sb.AppendFormat("{0,3} {1,-20} {2,-31} {3,5} {4,5} {5,6} {6,4}/{7,-5} {8,5}  {9}",
                i,
                phase.Length > 20 ? phase[..20] : phase,
                sys.Name.Length > 31 ? sys.Name[..31] : sys.Name,
                sys.PredecessorCount,
                deps,
                ready == 1 ? "yes" : "no",
                Math.Min(nextChunk, totalChunks),
                totalChunks,
                _remainingChunks[i].Value,
                skip == SkipReason.NotSkipped ? "" : skip.ToString());
            sb.AppendLine();
        }

        // List the stuck-systems' predecessor chains — most useful when one system fails to
        // decrement deps for some reason.
        sb.AppendLine();
        sb.AppendLine("Stuck systems (deps>0, ready==1 with chunks to claim, or every chunk claimed and some never counted down):");
        var anyStuck = false;
        for (var i = 0; i < AllSystemCount; i++)
        {
            var deps = _remainingDeps[i].Value;
            var ready = _isReady[i].Value;
            var word = _claims[i].Value;
            var nextChunk = (uint)word;
            var totalChunks = (uint)(word >> 32);

            // The second multi-chunk case is the one hang the claim word still allows: a claim whose chunk never reached its decrement — an exception that
            // escaped into WorkerLoop's safety net — leaves the word exhausted and _remainingChunks above zero, so nobody completes the system.
            var claimsLeft = nextChunk < totalChunks;
            var countdownLeft = totalChunks > 0 && !claimsLeft && _remainingChunks[i].Value > 0;
            var stuck = deps > 0 || (ready == 1 && (claimsLeft || countdownLeft));
            if (!stuck) continue;
            anyStuck = true;
            sb.Append("  ").Append(Systems[i].Name).Append("  pred=[");
            var first = true;
            for (var j = 0; j < AllSystemCount; j++)
            {
                foreach (var s in Systems[j].Successors)
                {
                    if (s != i) continue;
                    if (!first) sb.Append(", ");
                    first = false;
                    sb.Append(Systems[j].Name).Append(_currentTickSystemMetrics[j].SkipReason == SkipReason.NotSkipped && _remainingDeps[j].Value == 0 && _isReady[j].Value == 0 ? "(done)" : "(not done)");
                }
            }
            sb.Append("]").AppendLine();
        }
        if (!anyStuck)
        {
            sb.AppendLine("  (none — every system either completed or is unblocked but waiting for a worker)");
        }

        // Surface any captured exceptions so a hang caused by a system throwing surfaces the
        // stack trace inline — without this the user would have to check the logger output
        // (which may be filtered) to know what went wrong.
        sb.AppendLine();
        sb.AppendLine("Captured exceptions (most recent per system):");
        var anyEx = false;
        for (var i = 0; i < AllSystemCount; i++)
        {
            var ex = _lastSystemException[i];
            if (ex == null)
            {
                continue;
            }
            anyEx = true;
            sb.Append("  System ").Append(i).Append(" '").Append(Systems[i].Name).Append("':").AppendLine();
            sb.AppendLine(ex.ToString());
        }
        if (!anyEx)
        {
            sb.AppendLine("  (no exceptions captured)");
        }

        return sb.ToString();
    }
}
