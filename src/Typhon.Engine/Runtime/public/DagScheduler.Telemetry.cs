using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Typhon.Engine;

public sealed partial class DagScheduler
{
    /// <summary>
    /// Computes derived metrics from raw timestamps and records the tick into the telemetry ring buffer.
    /// Called at the end of every tick by the timer thread (single writer, no contention).
    /// </summary>
    private void ComputeAndRecordTelemetry(long tickStart, long tickEnd)
    {
        var tickDurationTicks = tickEnd - tickStart;
        var actualMs = (float)(tickDurationTicks * 1000.0 / Stopwatch.Frequency);
        var targetMs = 1000f / _options.BaseTickRate;
        var overrunRatio = actualMs / targetMs;

        // Effective ratio for the OverloadDetector — measured against the CURRENT multiplier-adjusted budget rather than the 1× base target. Issue #289
        // follow-up: with the base-rate ratio, a workload that fits comfortably at multiplier=N is still classified as "overrun" because it exceeds
        // the 1× target — so deescalation never fires and the engine is trapped at max throttle even when there's slack. Switching the detector to use the
        // effective ratio lets the multiplier self-stabilise: it ramps UP under base-rate overrun (because effective ratio at low mult ≈ base ratio), and
        // ramps DOWN once the throttled budget is comfortable.
        // The display-side `overrunRatio` (kind-242 event, level-change event, log warning, OverloadStrip chart) keeps the BASE-rate semantic — that's what
        // the user expects to see when they ask "is this tick overrunning the engine target?".
        var multiplierClamped = Math.Max(1, _tickMultiplier);
        var effectiveRatio = actualMs / (targetMs * multiplierClamped);

        // Tick-to-tick interval: the real period seen by the simulation
        var tickIntervalMs = 0f;
        if (_previousTickStart > 0)
        {
            tickIntervalMs = (float)((tickStart - _previousTickStart) * 1000.0 / Stopwatch.Frequency);
        }

        _previousTickStart = tickStart;

        var activeSystemCount = 0;
        var totalEntitiesProcessed = 0;

        for (var i = 0; i < AllSystemCount; i++)
        {
            ref var sm = ref _currentTickSystemMetrics[i];
            sm.SystemIndex = i;

            if (sm.FirstChunkGrabTick > 0 && sm.ReadyTick > 0)
            {
                activeSystemCount++;
                totalEntitiesProcessed += sm.EntitiesProcessed;
                sm.TransitionLatencyUs = TicksToUs(sm.FirstChunkGrabTick - sm.ReadyTick);
                sm.DurationUs = TicksToUs(sm.LastChunkDoneTick - sm.FirstChunkGrabTick);

                // Deep mode: straggler gap for multi-chunk systems (Pipeline and parallel QuerySystem)
                if (TelemetryConfig.SchedulerActive && TelemetryConfig.SchedulerTrackStragglerGap)
                {
                    var sys = Systems[i];
                    if ((sys.Type == SystemType.PipelineSystem || sys.IsParallelQuery) && sys.TotalChunks > 1 && sm.WorkersTouched > 0)
                    {
                        // Theoretical duration with perfect parallelism:
                        // total work divided evenly across participating workers
                        var parallelism = Math.Min(sm.WorkersTouched, sys.TotalChunks);
                        if (parallelism > 1)
                        {
                            // Estimate: total sequential work ≈ duration × parallelism, theoretical = that / parallelism
                            // More precisely: straggler = actual - (actual * parallelism / parallelism) = 0 in ideal.
                            // We approximate: theoretical = actual if perfectly balanced. The gap is the deviation.
                            // Use a simpler metric: chunk_work × totalChunks / parallelism (but we don't know chunk_work).
                            // Fall back to: gap = 0 when only 1 worker touched it, otherwise report raw duration as-is.
                            // For v1, just record duration. The straggler gap will be more meaningful when we have per-chunk timing data from actual
                            // Pipeline integration.
                            sm.StragglerGapUs = 0f; // Placeholder — refined in Pipeline integration (#196)
                        }
                    }
                }
            }
            else
            {
                if (sm.SkipReason == SkipReason.NotSkipped)
                {
                    sm.SkipReason = SkipReason.ShouldRunFalse;
                }
            }
        }

        // Capture event queue depth for telemetry and overload detection. Also emit per-(tick, queue)
        // QueueTickEnd records (#311) so the cache builder can fold them into the v12 QueueTickSummaries
        // section. Reading the accumulators here happens BEFORE the next tick's Reset() clears them.
        var queueDepth = 0;
        for (var i = 0; i < _eventQueues.Length; i++)
        {
            var q = _eventQueues[i];
            var endOfTickDepth = (uint)q.Count;
            queueDepth += (int)endOfTickDepth;
            TyphonEvent.EmitQueueTickEnd((uint)_currentTickNumber, q.QueueId, q.PeakDepth, endOfTickDepth, q.OverflowCount, q.Produced, q.Consumed);
        }

        // Update overload state machine — uses the EFFECTIVE ratio (vs current multiplier-adjusted target) so the detector can recognise headroom at high
        // multipliers and step the throttle back down. See the comment at the top of this method for why effectiveRatio ≠ overrunRatio under throttle.
        var previousLevel = _overloadDetector.CurrentLevel;
        var previousMul = _tickMultiplier;
        var levelChanged = _overloadDetector.Update(effectiveRatio, queueDepth);
        _tickMultiplier = _overloadDetector.TickMultiplier;

        if (levelChanged)
        {
            LogOverloadLevelChanged(previousLevel, _overloadDetector.CurrentLevel, _currentTickNumber);
            TyphonEvent.EmitSchedulerOverloadLevelChange((byte)previousLevel, (byte)_overloadDetector.CurrentLevel, overrunRatio, queueDepth, (byte)previousMul, (byte)_tickMultiplier);

            if (_overloadDetector.CurrentLevel == OverloadLevel.PlayerShedding && previousLevel != OverloadLevel.PlayerShedding)
            {
                OnCriticalOverloadCallback?.Invoke();
            }
        }

        // Per-tick OverloadDetector gauge (issue #289 follow-up). Carries the full state used to drive
        // escalation/deescalation decisions so an offline trace can audit *why* the engine throttled —
        // not just the level transitions (which only fire on change). Default-OFF leaf flag,
        // <c>Scheduler:Overload:Detector:Enabled</c>, controls emission.
        TyphonEvent.EmitSchedulerOverloadDetector(
            _currentTickNumber,
            overrunRatio,
            (ushort)Math.Min(_overloadDetector.ConsecutiveOverrunTicks, ushort.MaxValue),
            (ushort)Math.Min(_overloadDetector.ConsecutiveUnderrunTicks, ushort.MaxValue),
            (ushort)Math.Min(_overloadDetector.ConsecutiveQueueGrowthTicks, ushort.MaxValue),
            queueDepth,
            (byte)_overloadDetector.CurrentLevel,
            (byte)Math.Min(_tickMultiplier, byte.MaxValue));

        var tickTelemetry = new TickTelemetry
        {
            TickNumber = _currentTickNumber,
            TargetDurationMs = targetMs,
            ActualDurationMs = actualMs,
            OverrunRatio = overrunRatio,
            TickIntervalMs = tickIntervalMs,
            ActiveWorkerCount = _workerCount,
            ActiveSystemCount = activeSystemCount,
            TotalEntitiesProcessed = totalEntitiesProcessed,
            CurrentLevel = _overloadDetector.CurrentLevel,
            TickMultiplier = _tickMultiplier,
            EventQueueDepth = queueDepth
        };

        // Enrich with subscription metrics (Output phase duration, deltas pushed, overflows)
        TelemetryEnrichCallback?.Invoke(ref tickTelemetry);

        _telemetryRing.Record(in tickTelemetry, _currentTickSystemMetrics.AsSpan(0, AllSystemCount));

        // Warn on overrun
        if (overrunRatio > 1.0f)
        {
            LogTickOverrun(_currentTickNumber, actualMs, targetMs, overrunRatio);
        }

        _currentTickNumber++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float TicksToUs(long ticks) => (float)((double)ticks / Stopwatch.Frequency * 1_000_000.0);
}
