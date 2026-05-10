using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine.Internals;

internal readonly struct ViewRegistration
{
    public readonly IView View;
    public readonly byte ComponentTag;

    public ViewRegistration(IView view, byte componentTag)
    {
        View = view;
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<ViewRegistration> GetViewsForField(int fieldIndex)
    {
        var views = _viewsByField;
        if ((uint)fieldIndex >= (uint)views.Length)
        {
            return ReadOnlySpan<ViewRegistration>.Empty;
        }
        return views[fieldIndex];
    }

    public void RegisterView(IView view) => RegisterView(view, view.FieldDependencies, 0);

    public void RegisterView(IView view, int[] fieldIndices, byte componentTag)
    {
        lock (_writeLock)
        {
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
                    if (ReferenceEquals(existing[j].View, view) && existing[j].ComponentTag == componentTag)
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
                newArray[existing.Length] = new ViewRegistration(view, componentTag);
                _viewsByField[fieldIndex] = newArray;
            }
            _viewCount++;
        }
    }

    public void DeregisterView(IView view)
    {
        lock (_writeLock)
        {
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
                    if (ReferenceEquals(existing[j].View, view))
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
                    _viewsByField[f] = [];
                }
                else
                {
                    var newArray = new ViewRegistration[existing.Length - removeCount];
                    var k = 0;
                    for (var j = 0; j < existing.Length; j++)
                    {
                        if (!ReferenceEquals(existing[j].View, view))
                        {
                            newArray[k++] = existing[j];
                        }
                    }
                    _viewsByField[f] = newArray;
                }
            }
            if (removedAny)
            {
                _viewCount--;
            }
        }
    }
}