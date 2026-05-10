using MemoryPack;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Typhon.Protocol;

namespace Typhon.Engine.Internals;

/// <summary>
/// Orchestrates the Output phase: refreshes published Views, computes deltas, serializes per-client
/// <see cref="TickDeltaMessage"/>s, and enqueues them to client send buffers.
/// </summary>
/// <remarks>
/// <para>Runs on the timer/last-worker thread after <see cref="DatabaseEngine.WriteTickFence"/>.
/// No concurrent writers — all systems have completed.</para>
/// </remarks>
internal sealed partial class SubscriptionOutputPhase
{
    private readonly DatabaseEngine _engine;
    private readonly PublishedViewRegistry _viewRegistry;
    private readonly ClientConnectionManager _clientManager;
    private readonly SubscriptionServerOptions _options;
    private readonly ILogger _logger;
    private readonly DeltaBuilder _deltaBuilder = new();

    // Last tick's metrics (read by telemetry enrichment callback)
    internal float LastOutputPhaseMs;
    internal int LastDeltasPushed;
    internal int LastOverflowCount;

    // Reusable buffers (single-threaded — no contention)
    private readonly List<SubscriptionEvent> _eventBuffer = new(8);
    private readonly List<ViewDeltaMessage> _viewDeltaBuffer = new(8);
    private readonly List<PublishedView> _toSubscribe = new(4);
    private readonly List<PublishedView> _toUnsubscribe = new(4);
    private readonly List<ClientConnection> _deadClients = new(4);
    private readonly Dictionary<ushort, ViewDeltaMessage> _sharedDeltas = new();
    private readonly List<ComponentSnapshot> _syncSnapshotBuffer = new(16);

    // Signal for I/O flush thread
    internal ManualResetEventSlim FlushSignal { get; } = new(false, 0);

    internal SubscriptionOutputPhase(DatabaseEngine engine, PublishedViewRegistry viewRegistry, ClientConnectionManager clientManager,
        SubscriptionServerOptions options, ILogger logger)
    {
        _engine = engine;
        _viewRegistry = viewRegistry;
        _clientManager = clientManager;
        _options = options;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Execute the Output phase for a tick. Called from <see cref="TyphonRuntime.OnTickEndInternal"/> after WriteTickFence.
    /// </summary>
    internal void Execute(long tickNumber, OverloadLevel overloadLevel = OverloadLevel.Normal)
    {
        var publishedViews = _viewRegistry.PublishedViews;
        if (publishedViews.Length == 0)
        {
            return;
        }

        var clients = _clientManager.GetAll();
        if (_clientManager.Count == 0)
        {
            // No clients connected — still refresh Views to keep them current, but skip serialization
            RefreshAllViews(publishedViews);
            return;
        }

        var startTimestamp = Stopwatch.GetTimestamp();

        // Reset per-tick counters
        LastDeltasPushed = 0;
        LastOverflowCount = 0;

        // Collect and remove dead clients (disconnected between ticks)
        _deadClients.Clear();
        foreach (var client in clients)
        {
            if (client.IsDisposed)
            {
                _deadClients.Add(client);
            }
        }

        foreach (var dead in _deadClients)
        {
            // Clean up subscriber lists
            foreach (var view in dead.ActiveSubscriptions)
            {
                view.RemoveSubscriber(dead);
            }

            _clientManager.Remove(dead);
        }

        // Create read-only Transaction for component data access
        using var tx = _engine.CreateQuickTransaction();

        // Phase 1: Refresh all shared Views FIRST.
        // Must happen before subscription transitions because BeginSync captures the View's entity set for incremental sync snapshots. Without this,
        // BeginSync would capture a stale (empty) set.
        foreach (var published in publishedViews)
        {
            if (published.IsShared && published.SharedView != null)
            {
                published.SharedView.Refresh(tx);
            }
        }

        // Phase 2: Process subscription transitions (BeginSync now captures fresh entity sets)
        foreach (var client in clients)
        {
            if (client.IsDisposed)
            {
                continue;
            }

            ProcessSubscriptionTransitions(client, tx);
        }

        // Phase 3: Build shared View deltas from already-refreshed Views (no re-refresh)
        _sharedDeltas.Clear();
        foreach (var published in publishedViews)
        {
            if (!published.IsShared || published.SubscriberCount == 0)
            {
                continue;
            }

            // Overload throttling: skip non-Critical views based on level and priority
            if (ShouldSkipSubscription(published, overloadLevel, tickNumber))
            {
                continue;
            }

            var msg = _deltaBuilder.BuildFromRefreshedView(published.PublishedId, published.SharedView, tx, _engine);
            if (msg.HasValue)
            {
                _sharedDeltas[published.PublishedId] = msg.Value;
            }
        }

        // Phase 3: Pre-serialize common TickDelta for steady-state shared-only clients (serialize once, memcpy to N)
        byte[] commonFrame = null;
        var commonDeltaCount = 0;
        if (_sharedDeltas.Count > 0)
        {
            var sharedViews = new ViewDeltaMessage[_sharedDeltas.Count];
            var vi = 0;
            foreach (var vd in _sharedDeltas.Values)
            {
                sharedViews[vi++] = vd;
                commonDeltaCount += (vd.Added?.Length ?? 0) + (vd.Removed?.Length ?? 0) + (vd.Modified?.Length ?? 0);
            }

            var commonDelta = new TickDeltaMessage
            {
                TickNumber = tickNumber,
                Views = sharedViews
            };
            var commonPayload = MemoryPackSerializer.Serialize(commonDelta);

            // Build length-prefixed frame
            commonFrame = new byte[4 + commonPayload.Length];
            BitConverter.TryWriteBytes(commonFrame.AsSpan(0, 4), commonPayload.Length);
            commonPayload.CopyTo(commonFrame.AsSpan(4));
        }

        // Phase 4: Enqueue per-client — fast path for steady-state shared-only clients
        var anyFlushed = false;
        foreach (var client in clients)
        {
            if (client.IsDisposed)
            {
                continue;
            }

            // Fast path: steady-state client with no events, no syncing, all Active shared Views
            if (commonFrame != null && IsSteadyStateSharedOnly(client, _sharedDeltas))
            {
                LastDeltasPushed += commonDeltaCount;
                if (EnqueuePreSerialized(client, commonFrame))
                {
                    anyFlushed = true;
                }

                continue;
            }

            // Slow path: per-client serialization (has events, syncing, or per-client Views)
            var tickDelta = BuildClientTickDelta(client, tickNumber, tx, _sharedDeltas);
            if (tickDelta.HasValue)
            {
                if (tickDelta.Value.Views != null)
                {
                    foreach (var vd in tickDelta.Value.Views)
                    {
                        LastDeltasPushed += (vd.Added?.Length ?? 0) + (vd.Removed?.Length ?? 0) + (vd.Modified?.Length ?? 0);
                    }
                }

                if (EnqueueToClient(client, tickDelta.Value))
                {
                    anyFlushed = true;
                }
            }
        }

        // Commit read-only Transaction (no changes, just releases TSN)
        tx.Commit();

        // Phase 4: Signal I/O thread to flush send buffers
        if (anyFlushed)
        {
            FlushSignal.Set();
        }

        var elapsedMs = (float)((Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency);
        LastOutputPhaseMs = elapsedMs;

        if (elapsedMs > 2.0f)
        {
            LogOutputPhaseOverBudget(tickNumber, elapsedMs * 1000.0);
        }
    }

    /// <summary>Refresh all published Views to keep ring buffers drained (even when no clients are connected).</summary>
    private void RefreshAllViews(PublishedView[] publishedViews)
    {
        using var tx = _engine.CreateQuickTransaction(DurabilityMode.Deferred);
        foreach (var published in publishedViews)
        {
            if (published.IsShared && published.SharedView != null)
            {
                published.SharedView.Refresh(tx);
                published.SharedView.ClearDelta();
            }
        }

        tx.Commit();
    }

    /// <summary>Process pending subscription changes for a client.</summary>
    private void ProcessSubscriptionTransitions(ClientConnection client, Transaction tx)
    {
        var pending = client.ConsumePendingSubscriptions();
        if (pending == null)
        {
            return;
        }

        _eventBuffer.Clear();
        _toSubscribe.Clear();
        _toUnsubscribe.Clear();

        SubscriptionTransition.ComputeTransition(client.ActiveSubscriptions, pending, _eventBuffer, _toSubscribe, _toUnsubscribe);

        // Apply unsubscriptions
        foreach (var view in _toUnsubscribe)
        {
            view.RemoveSubscriber(client);
            client.ActiveSubscriptions.Remove(view);

            if (client.ViewStates.TryGetValue(view.PublishedId, out var state))
            {
                // Dispose per-client View if applicable
                state.PerClientView?.Dispose();
                client.ViewStates.Remove(view.PublishedId);
            }
        }

        // Apply subscriptions
        foreach (var view in _toSubscribe)
        {
            view.AddSubscriber(client);
            client.ActiveSubscriptions.Add(view);

            var state = new ViewSubscriptionState();
            client.ViewStates[view.PublishedId] = state;

            if (view.IsShared)
            {
                // Begin incremental sync from shared View's current entity set
                IncrementalSyncTracker.BeginSync(state, view.SharedView);
            }
            else
            {
                // Create per-client View
                var clientView = view.ViewFactory(client.Context);
                state.PerClientView = clientView;
                // Refresh to populate initial entity set
                clientView.Refresh(tx);
                IncrementalSyncTracker.BeginSync(state, clientView);
                clientView.ClearDelta();
            }
        }

        // Store events on client for inclusion in this tick's TickDelta
        client.PendingEvents.AddRange(_eventBuffer);
    }

    /// <summary>Build the complete TickDelta for a client.</summary>
    private TickDeltaMessage? BuildClientTickDelta(ClientConnection client, long tickNumber, Transaction tx, Dictionary<ushort, ViewDeltaMessage> sharedDeltas)
    {
        _eventBuffer.Clear();
        _viewDeltaBuffer.Clear();

        // Consume pending events from subscription transitions
        if (client.PendingEvents.Count > 0)
        {
            _eventBuffer.AddRange(client.PendingEvents);
            client.PendingEvents.Clear();
        }

        foreach (var (viewId, state) in client.ViewStates)
        {
            // Handle incremental sync
            if (state.Phase == SubscriptionPhase.Syncing)
            {
                var (batch, isComplete) = IncrementalSyncTracker.BuildSyncBatch(state, tx, _options.SyncBatchSize, _syncSnapshotBuffer);
                if (batch != null)
                {
                    _viewDeltaBuffer.Add(new ViewDeltaMessage
                    {
                        ViewId = viewId,
                        Added = batch
                    });
                }

                if (isComplete)
                {
                    state.Phase = SubscriptionPhase.Active;
                    _eventBuffer.Add(new SubscriptionEvent
                    {
                        ViewId = viewId,
                        Type = EventType.SyncComplete
                    });
                }

                continue; // No deltas during sync
            }

            // Handle resync
            if (state.NeedsResync)
            {
                state.NeedsResync = false;
                _eventBuffer.Add(new SubscriptionEvent
                {
                    ViewId = viewId,
                    Type = EventType.Resync
                });

                // Re-sync: begin incremental sync from current state
                var view = state.PerClientView ?? _viewRegistry.GetById(viewId)?.SharedView;
                if (view != null)
                {
                    IncrementalSyncTracker.BeginSync(state, view);
                }

                continue;
            }

            // Normal delta flow (Active phase)
            if (state.Phase != SubscriptionPhase.Active)
            {
                continue;
            }

            var published = _viewRegistry.GetById(viewId);
            if (published == null)
            {
                continue;
            }

            if (published.IsShared)
            {
                // Use shared delta (already computed once)
                if (sharedDeltas.TryGetValue(viewId, out var sharedDelta))
                {
                    _viewDeltaBuffer.Add(sharedDelta);
                }
            }
            else
            {
                // Per-client View: compute delta individually
                var delta = _deltaBuilder.BuildPerClientViewDelta(published, state.PerClientView, tx, _engine);
                if (delta.HasValue)
                {
                    _viewDeltaBuffer.Add(delta.Value);
                }
            }
        }

        if (_eventBuffer.Count == 0 && _viewDeltaBuffer.Count == 0)
        {
            return null;
        }

        return new TickDeltaMessage
        {
            TickNumber = tickNumber,
            Events = _eventBuffer.Count > 0 ? _eventBuffer.ToArray() : null,
            Views = _viewDeltaBuffer.Count > 0 ? _viewDeltaBuffer.ToArray() : null
        };
    }

    /// <summary>Serialize and enqueue a TickDelta to the client's send buffer.</summary>
    private bool EnqueueToClient(ClientConnection client, TickDeltaMessage tickDelta)
    {
        // Serialize with MemoryPack
        var serialized = MemoryPackSerializer.Serialize(tickDelta);

        // Length-prefixed frame: [4 bytes LE length][payload]
        var frameSize = 4 + serialized.Length;

        // Backpressure: check if frame fits in buffer
        if (client.Buffer.AvailableBytes < frameSize)
        {
            // Buffer full — drop delta, mark for resync
            MarkClientForResync(client);
            LastOverflowCount++;
            LogBackpressureOverflow(client.Context.ConnectionId);
            return false;
        }

        if (client.Buffer.FillPercentage >= _options.BackpressureWarningThreshold)
        {
            LogBackpressureWarning(client.Context.ConnectionId, client.Buffer.FillPercentage);
        }

        // Write length prefix (little-endian)
        Span<byte> lengthPrefix = stackalloc byte[4];
        BitConverter.TryWriteBytes(lengthPrefix, serialized.Length);

        if (!client.Buffer.TryWrite(lengthPrefix) || !client.Buffer.TryWrite(serialized))
        {
            // Race between AvailableBytes check and TryWrite (I/O thread consumed data between reads) — should be rare
            MarkClientForResync(client);
            LastOverflowCount++;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Check if a client is in steady state: all subscriptions Active, no pending events, no per-client Views, and subscribed to exactly the shared Views
    /// that have deltas.
    /// </summary>
    private static bool IsSteadyStateSharedOnly(ClientConnection client, Dictionary<ushort, ViewDeltaMessage> sharedDeltas)
    {
        if (client.PendingEvents.Count > 0)
        {
            return false;
        }

        // Must have subscriptions and they must all be Active shared Views
        if (client.ViewStates.Count == 0)
        {
            return false;
        }

        foreach ((_, ViewSubscriptionState state) in client.ViewStates)
        {
            if (state.Phase != SubscriptionPhase.Active)
            {
                return false;
            }

            if (state.PerClientView != null)
            {
                return false; // Has per-client View — can't use shared fast path
            }

            if (state.NeedsResync)
            {
                return false;
            }
        }

        // Client's subscribed View set must match the shared deltas set exactly
        if (client.ViewStates.Count != sharedDeltas.Count)
        {
            return false;
        }

        foreach (var viewId in client.ViewStates.Keys)
        {
            if (!sharedDeltas.ContainsKey(viewId))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Enqueue a pre-serialized length-prefixed frame to the client's send buffer.</summary>
    private bool EnqueuePreSerialized(ClientConnection client, byte[] frame)
    {
        if (client.Buffer.AvailableBytes < frame.Length)
        {
            MarkClientForResync(client);
            LastOverflowCount++;
            LogBackpressureOverflow(client.Context.ConnectionId);
            return false;
        }

        if (client.Buffer.FillPercentage >= _options.BackpressureWarningThreshold)
        {
            LogBackpressureWarning(client.Context.ConnectionId, client.Buffer.FillPercentage);
        }

        if (!client.Buffer.TryWrite(frame))
        {
            MarkClientForResync(client);
            LastOverflowCount++;
            return false;
        }

        return true;
    }

    private static void MarkClientForResync(ClientConnection client)
    {
        foreach (var (_, state) in client.ViewStates)
        {
            if (state is { Phase: SubscriptionPhase.Active })
            {
                state.NeedsResync = true;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Overload throttling
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Determines whether a published View's deltas should be skipped this tick due to overload.
    /// Views are still refreshed (ring buffers drained) — only delta building/pushing is skipped.
    /// </summary>
    private static bool ShouldSkipSubscription(PublishedView view, OverloadLevel level, long tickNumber)
    {
        if (level == OverloadLevel.Normal || view.Priority == SubscriptionPriority.Critical)
        {
            return false;
        }

        if (view.Priority == SubscriptionPriority.Low)
        {
            // Low: every 2nd tick at Level 1, every 4th tick at Level 2+
            var divisor = level >= OverloadLevel.ScopeReduction ? 4 : 2;
            return tickNumber % divisor != 0;
        }

        if (view.Priority == SubscriptionPriority.Normal && level >= OverloadLevel.ScopeReduction)
        {
            // Normal: every 2nd tick at Level 2+
            return tickNumber % 2 != 0;
        }

        return false;
    }

    // ═══════════════════════════════════════════════════════════════
    // Logging
    // ═══════════════════════════════════════════════════════════════

    [LoggerMessage(LogLevel.Warning, "Output phase exceeded 2ms budget: tick={TickNumber}, elapsed={ElapsedUs:F0}us")]
    private partial void LogOutputPhaseOverBudget(long tickNumber, double elapsedUs);

    [LoggerMessage(LogLevel.Warning, "Backpressure overflow: client={ConnectionId}, dropping delta and marking for resync")]
    private partial void LogBackpressureOverflow(int connectionId);

    [LoggerMessage(LogLevel.Warning, "Backpressure warning: client={ConnectionId}, buffer fill={FillPct:P0}")]
    private partial void LogBackpressureWarning(int connectionId, float fillPct);
}
