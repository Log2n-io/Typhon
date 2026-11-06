namespace Typhon.Engine.Internals;

/// <summary>
/// The owner of per-slot resources a closed session leaves behind, told when <see cref="SessionTable"/> recycles the slot (SUB-40): the frame assembler,
/// whose two pooled frame blocks a session holds in its send state from frame to frame.
/// </summary>
internal interface ISessionSlotRecycler
{
    /// <summary>
    /// A slot is being recycled: its gate is closed with no send in flight and its identity cleared, so nothing reads what it holds any more. On the tick's
    /// prologue, before any frame is produced.
    /// </summary>
    /// <param name="slot">The slot index.</param>
    void Recycled(int slot);
}
