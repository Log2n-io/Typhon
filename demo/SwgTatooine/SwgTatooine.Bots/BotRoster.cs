using System;
using System.Collections.Generic;

namespace SwgTatooine.Bots;

/// <summary>
/// The arithmetic that takes dead sessions out of a fleet's slot array, separated from the fleet so it can be tested.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because the fleet had no notion of a session DYING.</b> The population was "slots filled" — a count that only ever
/// went up with a handshake and down with a deliberate shrink. A session the server closed stayed in its slot and in the count
/// forever, so the reconciler compared a target of 8 000 against a live count that included thousands of corpses, decided it had
/// enough, and never re-opened anything. Measured on the control panel: 8 000 opened, 4 407 closed by the server for silence, the
/// dial stuck at 3 593 for ten minutes while the panel said "aiming for 8 000". One lost session was permanent.
/// </para>
/// <para>
/// <b>Swap-remove from the tail, not a stable compaction.</b> Order carries no meaning here — the account index lives on the bot,
/// not in its slot, which is what <see cref="BotIndexPool"/> is for — and a stable compaction would move every slot above the
/// victim while the driver and the ping loop are walking them. Moving one element per victim keeps the window as small as the
/// readers' null tolerance already covers.
/// </para>
/// <para>
/// <b>Scanned from the top down</b>, so the element swapped in from the tail has already been examined: scanning upward would move
/// an unexamined element into a slot the scan has passed and leave a dead session in the population.
/// </para>
/// </remarks>
public static class BotRoster
{
    /// <summary>Removes every slot the caller reports as no longer alive, taking each victim's place from the tail.</summary>
    /// <typeparam name="T">The slot type; the fleet's is its private bot.</typeparam>
    /// <param name="slots">The population array. Entries at or above <paramref name="count"/> are not read.</param>
    /// <param name="count">How much of <paramref name="slots"/> is published.</param>
    /// <param name="alive">Answers whether a slot's session is still up. A null slot is never passed to it and never counts as alive.</param>
    /// <param name="victims">Receives the removed entries, so the caller can release their indices and dispose them.</param>
    /// <returns>The new published count.</returns>
    /// <remarks>
    /// Vacated tail slots are nulled, which readers must already tolerate: a reader that captured the old count walks slots this
    /// method has emptied, whichever order they are written in. <c>BotSwarm.Bots</c> documents that contract and both of its
    /// per-session paths null-check.
    /// </remarks>
    public static int Compact<T>(T[] slots, int count, Func<T, bool> alive, List<T> victims)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentNullException.ThrowIfNull(alive);
        ArgumentNullException.ThrowIfNull(victims);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, slots.Length);

        var live = count;
        for (var i = count - 1; i >= 0; i--)
        {
            var slot = slots[i];
            if (slot != null && alive(slot))
            {
                continue;
            }

            if (slot != null)
            {
                victims.Add(slot);
            }

            // The tail element moves down into the hole. When it IS the hole this is a self-assignment, which is why the null comes
            // after: writing the null first would erase the survivor.
            var last = live - 1;
            slots[i] = slots[last];
            slots[last] = null;
            live = last;
        }

        return live;
    }
}
