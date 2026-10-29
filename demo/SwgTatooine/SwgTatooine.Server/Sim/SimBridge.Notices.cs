using System.Collections.Generic;
using Typhon.Protocol;

namespace SwgTatooine.Server;

/// <summary>
/// The notices a move of credits or items tells the players it concerns (<see cref="PlayerNotice"/>): queued where a ledger row is written, told by the
/// <c>Notices</c> system after every writer of the tick.
/// </summary>
/// <remarks>
/// <para>
/// <b>Queued at the row, told after the writers.</b> A row is written from four serial systems — the desk, the Ledger, Loot and the Market — some through
/// a side transaction with no session context at hand; the <c>Notices</c> system runs after all four in the same phase and tells what they queued, with
/// the other side's name read once per character a tick.
/// </para>
/// <para>
/// <b>Only to a character with a session.</b> The world, a listing and a character nobody plays have no session; their side is dropped at the telling.
/// A notice is news: one queued while its player is between sessions is not kept for later.
/// </para>
/// </remarks>
public sealed partial class SimBridge
{
    private readonly List<(long Character, long Other, PlayerNotice Notice)> _notices = [];
    private readonly Dictionary<EntityId, uint> _noticeSessions = [];
    private readonly Dictionary<long, Utf8Text256> _noticeNames = [];
    private readonly object _noticesLock = new();
    private ushort? _characterArchetype;

    /// <summary>Queues the notice of one ledger row to each of its sides that is not the world.</summary>
    private void QueueNotice(byte ledger, long from, long to, long world, string kind, long amount, int count, ushort template, byte mk, long reference)
    {
        // A measured run, and a check that serves nothing, has nobody to tell.
        if (!TatooineReplication.Declared)
        {
            return;
        }

        var notice = new PlayerNotice
        {
            Ledger = ledger,
            Mk = mk,
            Template = template,
            Count = count,
            Amount = amount,
            Ref = reference,
            Kind = KindText(kind),
            Name = ledger == PlayerNotice.Items ? ItemName(template) : default,
        };

        lock (_noticesLock)
        {
            if (from != world)
            {
                notice.Incoming = 0;
                _notices.Add((from, to, notice));
            }

            if (to != world)
            {
                notice.Incoming = 1;
                _notices.Add((to, from, notice));
            }
        }
    }

    // Each kind's name encoded once: Utf8Text256.From encodes, and a drop is several rows.
    private readonly Dictionary<string, Utf8Text256> _noticeKinds = [];

    private Utf8Text256 KindText(string kind)
    {
        lock (_noticeKinds)
        {
            if (!_noticeKinds.TryGetValue(kind, out var text))
            {
                text = Utf8Text256.From(kind);
                _noticeKinds[kind] = text;
            }

            return text;
        }
    }

    // Each item's name encoded once, by template.
    private readonly Dictionary<ushort, Utf8Text256> _noticeItems = [];

    private Utf8Text256 ItemName(ushort template)
    {
        lock (_noticeItems)
        {
            if (!_noticeItems.TryGetValue(template, out var text))
            {
                text = Utf8Text256.From(ItemCatalog.TryGet(template, out var t) && t.Id == template ? t.Name : "Unknown");
                _noticeItems[template] = text;
            }

            return text;
        }
    }

    /// <summary>Tells every queued notice to its player's session, once a tick, after every writer of a row.</summary>
    public void NoticesTick(TickContext ctx)
    {
        lock (_noticesLock)
        {
            if (_notices.Count == 0)
            {
                return;
            }

            TatooineReplication.FillCharacterSessions(_noticeSessions);
            _noticeNames.Clear();
            var tx = ctx.Transaction;
            foreach (var (character, other, queued) in _notices)
            {
                if (!_noticeSessions.TryGetValue(EntityId.FromRawValue((ulong)character), out var session))
                {
                    continue;
                }

                var notice = queued;
                notice.Other = NameOfOther(tx, other);
                TatooineReplication.Tell(ctx, session, in notice);
            }

            _notices.Clear();
        }
    }

    // The other side's character name, read once per character a tick; empty for the world, a listing, or a character that no longer exists.
    private Utf8Text256 NameOfOther(Transaction tx, long other)
    {
        if (_noticeNames.TryGetValue(other, out var name))
        {
            return name;
        }

        // Only a character has a name: the other side of a row may be a listing, whose archetype has no profile to read, and reading one there throws.
        var id = EntityId.FromRawValue((ulong)other);
        _characterArchetype ??= Dbe.ArchetypeIdOf<CharacterArch>();
        name = other != 0 && tx != null && id.ArchetypeId == _characterArchetype && tx.TryOpen(id, out var e)
            ? Utf8Text256.From(e.Read(CharacterArch.Profile).Name.AsString)
            : default;
        _noticeNames[other] = name;
        return name;
    }
}
