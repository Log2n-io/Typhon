// CS0282: split-partial-struct field ordering — benign for TraceEvent ref structs (codec encodes per-field, never as a blob). See #294.
#pragma warning disable CS0282

using Typhon.Profiler;

namespace Typhon.Engine.Internals;

/// <summary>Producer-side ref struct for <see cref="TraceEventKind.RuntimeTransactionLifecycle"/>.</summary>
[TraceEvent(TraceEventKind.RuntimeTransactionLifecycle, EmitEncoder = true)]
internal ref partial struct RuntimeTransactionLifecycleEvent
{
    [BeginParam]
    public ushort SysIdx;
    public uint TxDurUs;
    public byte Success;

}

