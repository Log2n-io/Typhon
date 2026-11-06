using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SwgTatooine.Server.Accounts;
using Typhon.Engine;
using Typhon.Protocol;

namespace SwgTatooine.Server;

/// <summary>A mail a player sends through <c>POST /swg/mail</c>: applied by the <c>Workshop</c>, answered when its transaction commits.</summary>
public sealed class MailRequest
{
    /// <summary>The sender's character raw id.</summary>
    public long From;

    /// <summary>The recipient's character name, as typed.</summary>
    public string To;

    public string Subject;

    public string Text;

    internal readonly TaskCompletionSource<MailOutcome> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // 0 queued, 1 taken by a Workshop pass, 2 given up by its caller (the admin request's reason: a mail answered "no tick" is never sent after all).
    private int _state;

    internal bool TryTake() => Interlocked.CompareExchange(ref _state, 1, 0) == 0;

    internal bool TryAbandon() => Interlocked.CompareExchange(ref _state, 2, 0) == 0;

    internal long Sent;
}

/// <summary>A mail request's answer: the mail's raw id, or the refusal's reason (a <c>TatooineReplication</c> reason code) and why.</summary>
public readonly record struct MailOutcome(bool Ok, long Mail, byte Reason, string Message);

/// <summary>
/// Mail (mail.md): each character's inbox — player mail, and the game's about installations and listings — written in the <c>Workshop</c>'s side transaction;
/// and /tell, said to the two sessions it concerns.
/// </summary>
/// <remarks>
/// <para>
/// <b>Each owner's inbox in memory, read on its first use</b> from the <c>Owner</c> index — never the whole table, the item cache's lesson (43 s for 1.4 M
/// stacks read in one tick). It counts an owner's mail in O(1) for the cap, sees the transaction's own sends, and is forgotten whole when a commit fails.
/// </para>
/// <para>
/// <b>One writer.</b> The game's mail is queued where it happens — the installations' sweep, the Market's sale and expiry — and written by the next
/// <c>Workshop</c> pass, after the Market in the same phase; a pass that does not commit queues it again. A player's mail arrives by HTTP: its text is
/// past the 512 bytes a command may carry, and the engine says such a thing is state, which belongs in the database.
/// </para>
/// </remarks>
public sealed partial class SimBridge
{
    private readonly Dictionary<long, List<(EntityId Id, long Sent, bool Read)>> _mailOf = [];
    private readonly ConcurrentQueue<MailRequest> _mailRequests = new();
    private readonly List<MailRequest> _mailApplied = [];
    private readonly List<(long Owner, byte Kind, long Ref, string Subject, string Text)> _systemMail = [];
    private readonly List<(long Owner, byte Kind, long Ref, string Subject, string Text)> _systemMailTaken = [];
    private readonly List<(long Owner, MailNotice Notice)> _mailNotices = [];

    // Mail spawned by the pass under way: one the cap drops again in the same pass is never told, its notice taken back.
    private readonly HashSet<EntityId> _mailWrittenThisPass = [];

    /// <summary>The players' mails one <c>Workshop</c> pass writes at most; the rest wait for the next. The endpoint's rate holds each sender to one a
    /// second.</summary>
    private const int MailRequestsPerPass = 64;

    /// <summary>
    /// The game's mails one pass writes at most; the rest wait for the next. A horde mails every character at once (horde.md § 3): ten thousand inserts and
    /// inbox reads in one tick's transaction is the tick a run stalls on, and 256 a pass spreads them over the tick rate's seconds.
    /// </summary>
    internal const int SystemMailPerPass = 256;
    private readonly List<(SessionId Session, ushort Seq)> _mailCommandsApplied = [];
    private readonly Dictionary<string, long> _characterByName = new(StringComparer.Ordinal);
    private readonly Dictionary<EntityId, uint> _mailSessions = [];
    private readonly object _systemMailLock = new();
    private IndexRef _mailByOwner;
    private IndexRef _accountByName;
    private IndexRef _characterByAccount;
    private bool _mailIndexes;

    // [0] player sent, [1] system sent, [2] read, [3] deleted, [4] dropped past the cap, [5] expired, [6] tells said, [7] tells refused.
    private readonly long[] _pendingMail = new long[8];
    private readonly long[] _mail = new long[8];
    private readonly long[] _systemMailBy = new long[8];
    private readonly long[] _pendingSystemMailBy = new long[8];

    /// <summary>Mails written since start: [player, system, read, deleted, dropped past the cap, expired, tells said, tells refused].</summary>
    internal long MailCount(int i) => Interlocked.Read(ref _mail[i]);

    /// <summary>Hands a player's mail to the simulation: answered when the <c>Workshop</c> pass that writes it commits.</summary>
    public Task<MailOutcome> SendPlayerMail(MailRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _mailRequests.Enqueue(request);
        return request.Done.Task;
    }

    /// <summary>Queues a mail from the game to <paramref name="owner"/>, written by the next <c>Workshop</c> pass. From any serial system of the
    /// tick.</summary>
    internal void QueueSystemMail(long owner, byte kind, long reference, string subject, string text)
    {
        lock (_systemMailLock)
        {
            _systemMail.Add((owner, kind, reference, subject, text));
        }
    }

    /// <summary>Whether mail waits to be written: the <c>Workshop</c> runs for it on a tick with nothing else to do.</summary>
    private bool MailPending
    {
        get
        {
            lock (_systemMailLock)
            {
                return _systemMail.Count > 0 || !_mailRequests.IsEmpty;
            }
        }
    }

    private void EnsureMailIndexes()
    {
        if (_mailIndexes)
        {
            return;
        }

        _mailByOwner = Dbe.GetIndexRef<MailCore, long>(m => m.Owner);
        _accountByName = Dbe.GetIndexRef<Account, String64>(a => a.Name);
        _characterByAccount = Dbe.GetIndexRef<Character, EntityLink<AccountArch>>(c => c.Account);
        _mailIndexes = true;
    }

    /// <summary>
    /// An owner's inbox, oldest first — by tick, then by id, the order the inbox endpoint lists it in — read from the index on its first use since a start
    /// or a failed commit, its mail past its life swept as it is read.
    /// </summary>
    private List<(EntityId Id, long Sent, bool Read)> MailOf(Transaction tx, long owner, long worldTick)
    {
        if (_mailOf.TryGetValue(owner, out var box))
        {
            return box;
        }

        EnsureMailIndexes();
        box = [];
        using (var found = tx.EnumerateIndex<MailCore, long>(_mailByOwner, owner, owner))
        {
            while (found.MoveNext())
            {
                var h = found.CurrentComponent;
                box.Add((EntityId.FromRawValue((ulong)found.CurrentEntityPK), h.SentTick, (h.Flags & MailFlags.Read) != 0));
            }
        }

        box.Sort(static (a, b) => a.Sent != b.Sent ? a.Sent.CompareTo(b.Sent) : a.Id.RawValue.CompareTo(b.Id.RawValue));
        _mailOf[owner] = box;
        DropExpired(tx, box, worldTick);
        return box;
    }

    /// <summary>The oldest mails past their life, destroyed: on an inbox's first read, and before each mail it receives.</summary>
    private void DropExpired(Transaction tx, List<(EntityId Id, long Sent, bool Read)> box, long worldTick)
    {
        var oldest = worldTick - Mail.LifeTicks(_config.TickRateHz);
        while (box.Count > 0 && box[0].Sent < oldest)
        {
            if (tx.TryOpen(box[0].Id, out _))
            {
                tx.Destroy(box[0].Id);
            }

            box.RemoveAt(0);
            _pendingMail[5]++;
        }
    }

    /// <summary>The character a name is, as a login reads it: the account's name, then its character. Cached once found.</summary>
    private bool TryCharacterNamed(Transaction tx, string name, out long character)
    {
        character = 0;
        if (!AccountName.TryNormalize(name, out var normalized))
        {
            return false;
        }

        if (_characterByName.TryGetValue(normalized, out character))
        {
            return true;
        }

        EnsureMailIndexes();
        var key = (String64)normalized;
        long account;
        using (var accounts = tx.EnumerateIndex<Account, String64>(_accountByName, key, key))
        {
            if (!accounts.MoveNext())
            {
                return false;
            }

            account = accounts.CurrentEntityPK;
        }

        using var characters = tx.EnumerateIndex<Character, long>(_characterByAccount, account, account);
        if (!characters.MoveNext())
        {
            return false;
        }

        character = characters.CurrentEntityPK;
        _characterByName[normalized] = character;
        return true;
    }

    /// <summary>
    /// Writes one mail into <paramref name="owner"/>'s inbox: its expired mail swept and, at the cap, its oldest dropped first. Queues its notice, told once
    /// the transaction commits.
    /// </summary>
    private long WriteMail(Transaction tx, long owner, long from, string fromName, byte kind, long reference, string subject, string text, long worldTick)
    {
        var box = MailOf(tx, owner, worldTick);
        DropExpired(tx, box, worldTick);
        while (box.Count >= Mail.MaxPerCharacter)
        {
            var dropped = box[0].Id;
            _pendingMail[4]++;
            if (tx.TryOpen(dropped, out _))
            {
                tx.Destroy(dropped);
            }

            box.RemoveAt(0);
            if (_mailWrittenThisPass.Remove(dropped))
            {
                _mailNotices.RemoveAll(n => n.Notice.Mail == dropped.RawValue);
            }
        }

        var header = new MailCore { Owner = owner, From = from, SentTick = worldTick, Ref = reference, Kind = kind, Subject = (String64)subject };
        if (!string.IsNullOrEmpty(text))
        {
            var utf8 = System.Text.Encoding.UTF8.GetBytes(text);
            using var bytes = tx.CreateComponentCollectionAccessor(ref header.Text);
            bytes.Replace(utf8);
        }

        var id = tx.Spawn<MailArch>(MailArch.Core.Set(in header));
        _mailWrittenThisPass.Add(id);

        box.Add((id, worldTick, false));
        var unread = 0;
        foreach (var m in box)
        {
            unread += m.Read ? 0 : 1;
        }

        _mailNotices.Add((owner, new MailNotice
        {
            Mail = id.RawValue,
            Kind = kind,
            Unread = (uint)unread,
            From = Utf8Text256.From(fromName ?? string.Empty),
            Subject = Utf8Text256.From(subject),
        }));
        return (long)id.RawValue;
    }

    /// <summary>The <c>Workshop</c>'s mail, in its side transaction: the game's queued, the players' sent, read, deleted; /tell said.</summary>
    private void MailTick(Transaction tx, Transaction tick0, TickContext ctx, SubscriptionsCommands subs)
    {
        lock (_systemMailLock)
        {
            var take = Math.Min(_systemMail.Count, SystemMailPerPass);
            _systemMailTaken.AddRange(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_systemMail)[..take]);
            _systemMail.RemoveRange(0, take);
        }

        foreach (var (owner, kind, reference, subject, text) in _systemMailTaken)
        {
            if (HasWallet(tx, owner))
            {
                WriteMail(tx, owner, 0, string.Empty, kind, reference, subject, text, ctx.WorldTick);
                _pendingMail[1]++;
                _pendingSystemMailBy[kind]++;
            }
        }

        for (var taken = 0; taken < MailRequestsPerPass && _mailRequests.TryDequeue(out var request); taken++)
        {
            if (!request.TryTake())
            {
                continue;   // given up by its caller: never sent
            }

            if (!TryCharacterNamed(tx, request.To, out var to))
            {
                request.Done.TrySetResult(new MailOutcome(false, 0, TatooineReplication.MailNoSuchPlayer, $"no player is called {request.To}"));
                continue;
            }

            if (to == request.From || !HasWallet(tx, to))
            {
                request.Done.TrySetResult(new MailOutcome(false, 0, TatooineReplication.MailBad,
                    to == request.From ? "a mail to yourself" : $"{request.To} has not played yet"));
                continue;
            }

            request.Sent = WriteMail(tx, to, request.From, NameOfOther(tx, request.From).ToString(), MailKind.Player, 0, request.Subject, request.Text,
                ctx.WorldTick);
            _pendingMail[0]++;
            _mailApplied.Add(request);
        }

        if (subs == null)
        {
            return;
        }

        foreach (var command in subs.Commands<ReadMail>())
        {
            if (!CraftActor(tick0, command.Session, out var owner, out _, out _, out _, out _))
            {
                RefuseWorkshop(subs, in command, TatooineReplication.NotInWorld);
                continue;
            }

            var box = MailOf(tx, owner, ctx.WorldTick);
            var at = -1;
            for (var i = 0; i < box.Count && at < 0; i++)
            {
                at = box[i].Id.RawValue == command.Value.Mail ? i : -1;
            }

            if (at < 0 || !tx.TryOpenMut(box[at].Id, out var e))
            {
                // Gone under the cache — a write outside this one writer — is forgotten, and the inbox read again on its next use.
                if (at >= 0)
                {
                    _mailOf.Remove(owner);
                }

                RefuseWorkshop(subs, in command, TatooineReplication.MailUnknown);
                continue;
            }

            if (!box[at].Read)
            {
                var h = e.Read(MailArch.Core);
                h.Flags |= MailFlags.Read;
                e.Set(MailArch.Core, h);
                box[at] = (box[at].Id, box[at].Sent, true);
                _pendingMail[2]++;
            }

            _mailCommandsApplied.Add((command.Session, command.Seq));
        }

        foreach (var command in subs.Commands<DeleteMail>())
        {
            if (!CraftActor(tick0, command.Session, out var owner, out _, out _, out _, out _))
            {
                RefuseWorkshop(subs, in command, TatooineReplication.NotInWorld);
                continue;
            }

            var box = MailOf(tx, owner, ctx.WorldTick);
            var gone = 0;
            for (var i = box.Count - 1; i >= 0; i--)
            {
                if (command.Value.Mail == 0 ? box[i].Read : box[i].Id.RawValue == command.Value.Mail)
                {
                    if (tx.TryOpen(box[i].Id, out _))
                    {
                        tx.Destroy(box[i].Id);
                        gone++;
                    }

                    box.RemoveAt(i);
                }
            }

            if (gone == 0 && command.Value.Mail != 0)
            {
                RefuseWorkshop(subs, in command, TatooineReplication.MailUnknown);
                continue;
            }

            _pendingMail[3] += gone;
            _mailCommandsApplied.Add((command.Session, command.Seq));
        }

        var tells = subs.Commands<TellPlayer>();
        if (tells.Count > 0)
        {
            TatooineReplication.FillCharacterSessions(_mailSessions);
        }

        foreach (var command in tells)
        {
            TellFromCommand(tx, tick0, ctx, subs, in command);
        }
    }

    private void TellFromCommand(Transaction tx, Transaction tick0, TickContext ctx, SubscriptionsCommands subs, in ClientCommand<TellPlayer> command)
    {
        if (!CraftActor(tick0, command.Session, out var from, out _, out _, out _, out _))
        {
            RefuseWorkshop(subs, in command, TatooineReplication.NotInWorld);
            return;
        }

        var text = ChatLines.Normalize(command.Value.Text);
        if (text.IsEmpty)
        {
            _pendingMail[7]++;
            RefuseWorkshop(subs, in command, TatooineReplication.MailBad);
            return;
        }

        var name = command.Value.To.ToString();
        if (!TryCharacterNamed(tx, name, out var to))
        {
            _pendingMail[7]++;
            RefuseWorkshop(subs, in command, TatooineReplication.MailNoSuchPlayer);
            return;
        }

        if (to == from)
        {
            _pendingMail[7]++;
            RefuseWorkshop(subs, in command, TatooineReplication.MailBad);
            return;
        }

        if (!_mailSessions.TryGetValue(EntityId.FromRawValue((ulong)to), out var session))
        {
            _pendingMail[7]++;
            RefuseWorkshop(subs, in command, TatooineReplication.TellOffline);
            return;
        }

        // Said now, whatever the pass's commit: a /tell writes nothing.
        TatooineReplication.Tell(ctx, session, new Whisper { Other = NameOfOther(tx, from), Text = text });
        TatooineReplication.Tell(ctx, command.Session.Value, new Whisper { Other = NameOfOther(tx, to), Text = text, Outgoing = 1 });
        _pendingMail[6]++;
    }

    /// <summary>
    /// The <c>Workshop</c>'s transaction ended: committed, the players' mail answered and every notice told to its owner's session; not, the requests
    /// refused, the game's mail queued again, the inboxes forgotten.
    /// </summary>
    private void EndMail(TickContext ctx, SubscriptionsCommands subs, bool committed)
    {
        if (committed)
        {
            for (var i = 0; i < _pendingMail.Length; i++)
            {
                Interlocked.Add(ref _mail[i], _pendingMail[i]);
                Interlocked.Add(ref _systemMailBy[i], _pendingSystemMailBy[i]);
            }

            foreach (var request in _mailApplied)
            {
                request.Done.TrySetResult(new MailOutcome(true, request.Sent, 0, "sent"));
            }

            if (_mailNotices.Count > 0)
            {
                TatooineReplication.FillCharacterSessions(_mailSessions);
                foreach (var (owner, notice) in _mailNotices)
                {
                    if (_mailSessions.TryGetValue(EntityId.FromRawValue((ulong)owner), out var session))
                    {
                        TatooineReplication.Tell(ctx, session, in notice);
                    }
                }
            }
        }
        else
        {
            foreach (var request in _mailApplied)
            {
                request.Done.TrySetResult(new MailOutcome(false, 0, TatooineReplication.CraftFailed, "the tick's transaction did not commit: try again"));
            }

            foreach (var (session, seq) in _mailCommandsApplied)
            {
                subs?.Reject(session, seq, TatooineReplication.CraftFailed);
            }

            lock (_systemMailLock)
            {
                _systemMail.InsertRange(0, _systemMailTaken);
            }

            _mailOf.Clear();
        }

        Array.Clear(_pendingMail);
        Array.Clear(_pendingSystemMailBy);
        _mailApplied.Clear();
        _mailCommandsApplied.Clear();
        _mailNotices.Clear();
        _mailWrittenThisPass.Clear();
        _systemMailTaken.Clear();
    }

    private long _mailWorldTick;

    /// <summary>The world tick of the last <c>Workshop</c> pass, run or skipped: what the inbox endpoint ages mail by.</summary>
    internal long MailWorldTick => Volatile.Read(ref _mailWorldTick);

    /// <summary>Publishes the tick the inbox endpoint ages mail by: every Workshop pass, before it decides there is nothing to do.</summary>
    private void PublishMailTick(long worldTick) => Volatile.Write(ref _mailWorldTick, worldTick);

    /// <summary>The mail counters as the JSON object <c>/swg/stats</c> carries under <c>mail</c> (mail.md § 8).</summary>
    internal void AppendMailJson(System.Text.StringBuilder j)
    {
        j.Append("{\"player\":").Append(MailCount(0)).Append(",\"system\":").Append(MailCount(1)).Append(",\"read\":").Append(MailCount(2))
            .Append(",\"deleted\":").Append(MailCount(3)).Append(",\"dropped\":").Append(MailCount(4)).Append(",\"expired\":").Append(MailCount(5))
            .Append(",\"tells\":").Append(MailCount(6)).Append(",\"tellsRefused\":").Append(MailCount(7)).Append(",\"systemBy\":{");
        for (byte k = MailKind.HopperFull; k <= MailKind.Expired; k++)
        {
            j.Append(k == MailKind.HopperFull ? "" : ",").Append('"').Append(MailKind.NameOf(k)).Append("\":").Append(Interlocked.Read(ref _systemMailBy[k]));
        }

        j.Append("}}");
    }
}
