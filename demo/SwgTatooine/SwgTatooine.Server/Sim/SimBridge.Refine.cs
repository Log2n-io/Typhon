using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;

namespace SwgTatooine.Server;

/// <summary>
/// Refine at a crafting station (crafting-catalog.md § 11.1; <see cref="Refining"/>), in the Workshop's transaction: the fee first, then N × count units of
/// Mk <i>k</i> destroyed and count of Mk <i>k</i>+1 minted, every row a <see cref="ItemMoveKind.Refine"/> naming the first — one commit, or nothing.
/// </summary>
public sealed partial class SimBridge
{
    private readonly List<ClientCommand<RefineItems>> _refinesApplied = [];

    // The transaction under way's, published when it commits: refines, the units they made by the Mk made, their fees.
    private long _pendingRefines;
    private readonly long[] _pendingRefinedByMk = new long[6];
    private long _pendingRefineFees;

    private long _refines;
    private readonly long[] _refinedByMk = new long[6];
    private long _refineFees;

    /// <summary>Refines applied since start.</summary>
    public long Refines => Interlocked.Read(ref _refines);

    /// <summary>Units refined into Mk <paramref name="mk"/> (II-V) since start.</summary>
    public long RefinedInto(byte mk) => mk is >= 2 and <= 5 ? Interlocked.Read(ref _refinedByMk[mk]) : 0;

    /// <summary>Refine fees paid since start, credits.</summary>
    public long RefineFees => Interlocked.Read(ref _refineFees);

    private void RefineFromCommand(Transaction tx, Transaction tick0, SubscriptionsCommands subs, in ClientCommand<RefineItems> command, long worldTick)
    {
        if (!CraftActor(tick0, command.Session, out var owner, out var body, out var realm, out var x, out var z))
        {
            RefuseCraft(subs, in command, TatooineReplication.NotInWorld);
            return;
        }

        if (!StationNear(realm, x, z))
        {
            RefuseCraft(subs, in command, TatooineReplication.CraftNotAtStation);
            return;
        }

        var v = command.Value;
        var ratio = Refining.RatioOf(v.Template, v.Mk);
        if (ratio == 0 || v.Count < 1 || v.Count > Refining.MaxOutputs(v.Template))
        {
            RefuseCraft(subs, in command, TatooineReplication.RefineBad);
            return;
        }

        var items = ItemsOf(owner);
        var inputs = ratio * v.Count;
        var held = UnitsOf(items, v.Template, v.Mk);
        if (held < inputs)
        {
            RefuseCraft(subs, in command, TatooineReplication.CraftBadInputs);
            return;
        }

        var next = (byte)(v.Mk + 1);
        if (!RoomForRefine(items, v.Template, v.Mk, held == inputs, v.Count))
        {
            RefuseCraft(subs, in command, TatooineReplication.CraftNoRoom);
            return;
        }

        var fee = Refining.FeeOf(v.Template, v.Mk, v.Count);
        if (fee > 0 && BalanceOf(tx, owner) < fee)
        {
            RefuseCraft(subs, in command, TatooineReplication.CraftNotEnough);
            return;
        }

        // Validated: every row of it names its first, the fee's too.
        var reference = _itemHead.LastSeq + 1;
        if (ItemTransfer(tx, owner, ItemLedger.World, v.Template, v.Mk, inputs, ItemMoveKind.Refine, reference, worldTick) != inputs
            || ItemTransfer(tx, ItemLedger.World, owner, v.Template, next, v.Count, ItemMoveKind.Refine, reference, worldTick) != v.Count
            || (fee > 0 && !Transfer(tx, owner, EconomyLedger.World, fee, TransferKind.Refine, reference, worldTick)))
        {
            _craftAbort = true;
            _refinesApplied.Add(command);
            return;
        }

        _pendingRefines++;
        _pendingRefinedByMk[next] += v.Count;
        _pendingRefineFees += fee;
        _craftBodies.Add(body);
        if (fee > 0)
        {
            _craftWalletsMoved.Add(body);
        }

        _refinesApplied.Add(command);
    }

    /// <summary>
    /// Whether <paramref name="outputs"/> of the next Mk fit: the room in its stacks, then free slots — the grid's, and those of the input's stacks when the
    /// refine takes every unit of it. A take that leaves some does not count the slots it may empty, so a refine is refused rather than found short.
    /// </summary>
    private static bool RoomForRefine(OwnerItems items, ushort template, byte mk, bool takesAll, int outputs)
    {
        var next = (byte)(mk + 1);
        var room = 0;
        for (var s = 1; s <= ItemLedger.Slots; s++)
        {
            ref readonly var slot = ref items.Slots[s];
            if (slot.Count == 0 || (takesAll && !slot.Instance && slot.Template == template && slot.Mk == mk))
            {
                room += ItemLedger.StackMax;
            }
            else if (!slot.Instance && slot.Template == template && slot.Mk == next)
            {
                room += ItemLedger.StackMax - slot.Count;
            }

            if (room >= outputs)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The Workshop's transaction ended: its refines counted, or refused because nothing of them was written.</summary>
    private void EndRefines(SubscriptionsCommands subs, bool committed)
    {
        if (committed)
        {
            Interlocked.Add(ref _refines, _pendingRefines);
            Interlocked.Add(ref _refineFees, _pendingRefineFees);
            for (var mk = 2; mk <= 5; mk++)
            {
                if (_pendingRefinedByMk[mk] != 0)
                {
                    Interlocked.Add(ref _refinedByMk[mk], _pendingRefinedByMk[mk]);
                }
            }
        }
        else
        {
            foreach (var c in _refinesApplied)
            {
                RefuseWorkshop(subs, in c, TatooineReplication.CraftFailed);
            }
        }

        _refinesApplied.Clear();
        _pendingRefines = 0;
        _pendingRefineFees = 0;
        System.Array.Clear(_pendingRefinedByMk);
    }

    /// <summary>The refine counters, as the <c>craft</c> section of <c>/swg/stats</c> carries them.</summary>
    private void AppendRefineJson(StringBuilder j)
    {
        var inv = CultureInfo.InvariantCulture;
        j.Append("\"refines\":").Append(Refines.ToString(inv)).Append(",\"refineFees\":").Append(RefineFees.ToString(inv)).Append(",\"refinedInto\":[");
        for (byte mk = 2; mk <= 5; mk++)
        {
            j.Append(mk == 2 ? "" : ",").Append(RefinedInto(mk).ToString(inv));
        }

        j.Append("],\"packs\":").Append(Packs.ToString(inv)).Append(",\"unpacked\":").Append(Unpacked.ToString(inv));
        j.Append(",\"grid\":{\"moves\":").Append(GridMoves.ToString(inv)).Append(",\"splits\":").Append(GridSplits.ToString(inv))
            .Append(",\"merges\":").Append(GridMerges.ToString(inv)).Append(",\"swaps\":").Append(GridSwaps.ToString(inv))
            .Append(",\"discards\":").Append(GridDiscards.ToString(inv)).Append('}');
    }
}
