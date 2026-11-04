using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;

namespace SwgTatooine.Server;

/// <summary>
/// What crafting made (Loïc, 2026-11-02: the dashboard's crafting card): every result by template and Mk since start, and the latest crafts — who, what, the
/// stats it came out with, on which tick — for <c>/swg/stats</c>'s <c>craft</c> section.
/// </summary>
/// <remarks>
/// Noted by the finish in the workshop's transaction and published when it commits, as every other count of the workshop: a craft that did not commit was
/// not made. The latest list is a small ring behind a lock, written once a commit and read by the stats endpoint off the tick; the crafter's name is read
/// there, through a quick transaction on the Versioned character (<c>EW-02</c>), not by the tick.
/// </remarks>
public sealed partial class SimBridge
{
    /// <summary>How many of the latest crafts <c>/swg/stats</c> lists.</summary>
    public const int RecentCraftsKept = 20;

    // AtUnixMs: when its commit published it, for the dashboard's "ago" — a world tick is no clock a reader can compare with.
    private readonly record struct MadeCraft(long Tick, long Crafter, ushort Template, byte Mk, ItemInstance Stats, bool Instance, long AtUnixMs = 0);

    private readonly List<MadeCraft> _pendingMade = [];
    private readonly long[] _craftsMadeBy = new long[ItemKeys];
    private readonly Queue<MadeCraft> _recentCrafts = new(RecentCraftsKept);
    private readonly Lock _recentCraftsLock = new();
    private readonly ConcurrentDictionary<long, string> _crafterNames = new();
    private int _craftsUnderWay;

    /// <summary>Crafts started and not yet finished, across every crafter, as of the workshop's last commit.</summary>
    public int CraftsUnderWay => Volatile.Read(ref _craftsUnderWay);

    /// <summary>Results of (<paramref name="template"/>, <paramref name="mk"/>) crafted since start.</summary>
    public long CraftsMade(ushort template, byte mk) => Interlocked.Read(ref _craftsMadeBy[KeyOf(template, mk)]);

    private void NoteMade(long crafter, ushort template, byte mk, in ItemInstance stats, bool instance, long worldTick)
        => _pendingMade.Add(new MadeCraft(worldTick, crafter, template, mk, stats, instance));

    /// <summary>The workshop's transaction ended: what it finished counted and listed, or — not committed — forgotten with it.</summary>
    private void EndMade(bool committed)
    {
        if (committed && _pendingMade.Count > 0)
        {
            var now = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            lock (_recentCraftsLock)
            {
                foreach (var made in _pendingMade)
                {
                    Interlocked.Increment(ref _craftsMadeBy[KeyOf(made.Template, made.Mk)]);
                    if (_recentCrafts.Count == RecentCraftsKept)
                    {
                        _recentCrafts.Dequeue();
                    }

                    _recentCrafts.Enqueue(made with { AtUnixMs = now });
                }
            }
        }

        _pendingMade.Clear();
        Volatile.Write(ref _craftsUnderWay, _crafts.Count);
    }

    /// <summary><c>,"made":{"blaster_dl44":[i,ii,iii,iv,v],…},"recent":[…newest first…]</c>.</summary>
    private void AppendMadeJson(StringBuilder j)
    {
        ByTemplate(j, "made", _craftsMadeBy);
        MadeCraft[] recent;
        lock (_recentCraftsLock)
        {
            recent = [.. _recentCrafts];
        }

        var inv = CultureInfo.InvariantCulture;
        j.Append(",\"recent\":[");
        for (var i = recent.Length - 1; i >= 0; i--)
        {
            var m = recent[i];
            ItemCatalog.TryGet(m.Template, out var t);
            j.Append(i == recent.Length - 1 ? "{" : ",{")
                .Append("\"tick\":").Append(m.Tick.ToString(inv))
                .Append(",\"at\":").Append(m.AtUnixMs.ToString(inv))
                .Append(",\"crafter\":\"").Append(EconomyEndpoints.Escape(CrafterName(m.Crafter)))
                .Append("\",\"item\":\"").Append(t.Asset)
                .Append("\",\"name\":\"").Append(EconomyEndpoints.Escape(t.Name ?? "?"))
                .Append("\",\"category\":\"").Append(ItemCategory.NameOf(t.Category))
                .Append("\",\"mk\":").Append(m.Mk);
            if (m.Instance)
            {
                var stats = m.Stats;
                ItemEndpoints.Stats(j, in t, in stats);
            }

            j.Append('}');
        }

        j.Append(']');
    }

    // A crafter's character name, read once off the tick and remembered: a name does not change.
    private string CrafterName(long crafter)
    {
        if (_crafterNames.TryGetValue(crafter, out var name))
        {
            return name;
        }

        var id = EntityId.FromRawValue((ulong)crafter);
        using (var tx = Dbe.CreateQuickTransaction())
        {
            name = id.ArchetypeId == Dbe.ArchetypeIdOf<CharacterArch>() && tx.TryOpen(id, out var e) ? e.Read(CharacterArch.Profile).Name.AsString : "?";
        }

        _crafterNames[crafter] = name;
        return name;
    }
}
