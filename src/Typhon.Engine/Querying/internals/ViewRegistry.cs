using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine.Internals;

internal readonly struct ViewRegistration
{
    /// <summary>
    /// The registered view's id — its identity in the registry. NOT the view: a registry that held the view would root it, and a view abandoned without
    /// <c>Dispose</c> would stay alive and subscribed for the life of the engine, every commit appending to it (#865).
    /// </summary>
    public readonly int ViewId;
    /// <summary>
    /// The view's delta buffer, supplied by the caller at registration time (callers know which concrete view they're
    /// registering and can read the buffer off the concrete type). Hot-path code (transaction commit, index maintainer) reads it directly, and tests
    /// <see cref="ViewDeltaRingBuffer.IsClosed"/> on it before appending — the one load that replaced the view's <c>IsDisposed</c>.
    /// </summary>
    public readonly ViewDeltaRingBuffer DeltaBuffer;
    public readonly byte ComponentTag;

    public ViewRegistration(int viewId, ViewDeltaRingBuffer deltaBuffer, byte componentTag)
    {
        ViewId = viewId;
        DeltaBuffer = deltaBuffer;
        ComponentTag = componentTag;
    }
}

internal class ViewRegistry
{
    private readonly ViewRegistration[][] _viewsByField;     // [fieldIndex] -> ViewRegistration[] (copy-on-write)
    private readonly Lock _writeLock = new();
    private int _viewCount;

    public ViewRegistry(int fieldCount)
    {
        _viewsByField = new ViewRegistration[fieldCount][];
        for (var i = 0; i < fieldCount; i++)
        {
            _viewsByField[i] = [];
        }
    }

    public int ViewCount => _viewCount;

    public int FieldCount => _viewsByField.Length;

    /// <remarks>
    /// <b>Acquire, not a plain load.</b> The publisher reads a registration's <c>DeltaBuffer</c> through this span with no lock; the registering
    /// thread release-stores the new array. Without the matching acquire the loads of the registration's fields have nothing to pair with, and on
    /// arm64 nothing stops them floating above the array load. <c>ArchetypeMembershipRegistry.Views</c> has carried this reasoning since #790; this
    /// channel is older and never got it. Free on x64, where acquire folds to a plain <c>mov</c>.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<ViewRegistration> GetViewsForField(int fieldIndex)
    {
        // The outer array is readonly and never replaced — only its ELEMENTS are swapped, so only the element load needs the acquire.
        var views = _viewsByField;
        if ((uint)fieldIndex >= (uint)views.Length)
        {
            return ReadOnlySpan<ViewRegistration>.Empty;
        }
        return Volatile.Read(ref views[fieldIndex]);
    }

    public void RegisterView(IView view, ViewDeltaRingBuffer deltaBuffer)
        => RegisterView(view, deltaBuffer, view.FieldDependencies, 0);

    public void RegisterView(IView view, ViewDeltaRingBuffer deltaBuffer, int[] fieldIndices, byte componentTag)
    {
        lock (_writeLock)
        {
            SweepAbandonedLocked();
            // Phase 7: ECS:View:Registry:Register instant — emitted once per RegisterView call.
            // fieldIdx = first dependency field for context; regCount = total view count after this registration.
            TyphonEvent.EmitEcsViewRegistryRegister(
                (ushort)Math.Min(view.ViewId, ushort.MaxValue),
                fieldIndices.Length > 0 ? (ushort)Math.Min(fieldIndices[0], ushort.MaxValue) : (ushort)0,
                (ushort)Math.Min(_viewCount + 1, ushort.MaxValue));
            for (var i = 0; i < fieldIndices.Length; i++)
            {
                var fieldIndex = fieldIndices[i];
                if ((uint)fieldIndex >= (uint)_viewsByField.Length)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(view), $"View {view.ViewId} declares field dependency {fieldIndex} but registry only has {_viewsByField.Length} fields.");
                }

                var existing = _viewsByField[fieldIndex];

                // Idempotent: skip if already present with same view reference and tag
                var found = false;
                for (var j = 0; j < existing.Length; j++)
                {
                    if (existing[j].ViewId == view.ViewId && existing[j].ComponentTag == componentTag)
                    {
                        found = true;
                        break;
                    }
                }
                if (found)
                {
                    continue;
                }

                // Copy-on-write: create new array +1
                var newArray = new ViewRegistration[existing.Length + 1];
                Array.Copy(existing, newArray, existing.Length);
                newArray[existing.Length] = new ViewRegistration(view.ViewId, deltaBuffer, componentTag);
                // Release: every field of the new registration must be visible before the array reference that names it.
                Volatile.Write(ref _viewsByField[fieldIndex], newArray);
            }
            _viewCount++;
        }
    }

    public void DeregisterView(IView view)
    {
        lock (_writeLock)
        {
            SweepAbandonedLocked();
            // Phase 7: ECS:View:Registry:Deregister instant.
            TyphonEvent.EmitEcsViewRegistryDeregister((ushort)Math.Min(view.ViewId, ushort.MaxValue), 0, (ushort)Math.Min(Math.Max(_viewCount - 1, 0), ushort.MaxValue));
            var removedAny = false;

            // Scan all field slots to find and remove ALL registrations for this view (may have multiple tags)
            for (var f = 0; f < _viewsByField.Length; f++)
            {
                var existing = _viewsByField[f];

                // Count how many registrations match this view (may differ by ComponentTag)
                var removeCount = 0;
                for (var j = 0; j < existing.Length; j++)
                {
                    if (existing[j].ViewId == view.ViewId)
                    {
                        removeCount++;
                    }
                }

                if (removeCount == 0)
                {
                    continue;
                }

                removedAny = true;
                if (removeCount == existing.Length)
                {
                    Volatile.Write(ref _viewsByField[f], []);
                }
                else
                {
                    var newArray = new ViewRegistration[existing.Length - removeCount];
                    var k = 0;
                    for (var j = 0; j < existing.Length; j++)
                    {
                        if (existing[j].ViewId != view.ViewId)
                        {
                            newArray[k++] = existing[j];
                        }
                    }
                    Volatile.Write(ref _viewsByField[f], newArray);
                }
            }
            if (removedAny)
            {
                _viewCount--;
            }
        }
    }

    /// <summary>Test hook: invoked before the sweep scans each field slot, so a test can land a finalizer's flag between two slots of one view.</summary>
    internal static Action<int> SweepFieldProbe;

    /// <summary>
    /// Removes every registration whose view was finalized without <c>Dispose</c> (<see cref="ViewDeltaRingBuffer.IsAbandoned"/>) and retires its buffer
    /// (#865). Caller holds <see cref="_writeLock"/>.
    /// </summary>
    /// <remarks>
    /// Runs on every registration and deregistration — the cold path, already under the lock — so the cost of a leaked view is bounded by the time to
    /// the next GC plus the next registration, instead of every commit for the life of the engine. An engine that stops creating views stops sweeping,
    /// but by then its publishers already skip the abandoned buffers on <see cref="ViewDeltaRingBuffer.IsClosed"/>: what remains is the entry, not the cost.
    /// </remarks>
    private void SweepAbandonedLocked()
    {
        List<ViewRegistration> removed = null;
        for (var f = 0; f < _viewsByField.Length; f++)
        {
            SweepFieldProbe?.Invoke(f);
            var existing = _viewsByField[f];
            var removeCount = 0;
            for (var j = 0; j < existing.Length; j++)
            {
                if (existing[j].DeltaBuffer.IsAbandoned)
                {
                    removeCount++;
                }
            }

            if (removeCount == 0)
            {
                continue;
            }

            var newArray = removeCount == existing.Length ? [] : new ViewRegistration[existing.Length - removeCount];
            var k = 0;
            for (var j = 0; j < existing.Length; j++)
            {
                var reg = existing[j];
                if (!reg.DeltaBuffer.IsAbandoned)
                {
                    newArray[k++] = reg;
                    continue;
                }

                removed ??= [];
                if (!ContainsView(removed, reg.ViewId))
                {
                    removed.Add(reg);
                }
            }

            // Abandonment is monotonic, so a view abandoned between the two passes can only shorten the copy, never overrun it.
            Volatile.Write(ref _viewsByField[f], k == newArray.Length ? newArray : newArray[..k]);
        }

        if (removed == null)
        {
            return;
        }

        // A view is counted, traced and retired once, when its LAST field entry leaves. The finalizer flags it outside this lock, so a sweep can scan
        // one of its fields before the flag lands and another after: it then leaves this sweep with entries still listed, and the next sweep removes
        // them. Decrementing per sweep counted it twice, and a ViewCount of zero makes the enabled-bits publisher skip every live view (review, #865).
        // Retire only then, too: the retire stamp is what separates publishers that could still hold a registration naming the buffer from those that
        // cannot (ViewBufferReclaimer, MEMB-04).
        foreach (var reg in removed)
        {
            if (IsListed(reg.ViewId))
            {
                continue;
            }

            _viewCount--;
            TyphonEvent.EmitEcsViewRegistryDeregister((ushort)Math.Min(reg.ViewId, ushort.MaxValue), 0,
                (ushort)Math.Min(Math.Max(_viewCount, 0), ushort.MaxValue));
            reg.DeltaBuffer.RetireAbandoned();
        }
    }

    private bool IsListed(int viewId)
    {
        for (var f = 0; f < _viewsByField.Length; f++)
        {
            var regs = _viewsByField[f];
            for (var j = 0; j < regs.Length; j++)
            {
                if (regs[j].ViewId == viewId)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool ContainsView(List<ViewRegistration> regs, int viewId)
    {
        foreach (var reg in regs)
        {
            if (reg.ViewId == viewId)
            {
                return true;
            }
        }

        return false;
    }
}