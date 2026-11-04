using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace SwgTatooine.Server;

/// <summary>
/// The shuttle fare (crafting-catalog.md § 15.4, Loïc 2026-11-03): a player passenger pays World <see cref="TatooineData.ShuttleFare"/> for a flight — the
/// sink § 15.4 counts at half a flight an hour.
/// </summary>
/// <remarks>
/// <para>
/// <b>Paid when the server accepts the boarding, in the Ledger's transaction, and flown only on a paid ticket.</b> <c>PlayerIntents</c> (Input) queues the
/// passenger; this tick's Ledger (Economy) charges it, or cancels the boarding and refuses the ask with <see cref="TatooineReplication.ShuttleFareUnpaid"/>;
/// <c>Shuttle</c> (Spawn, which runs before Economy) flies a session's passenger only once it holds a ticket — so on the next tick at the soonest, and never
/// unpaid. The scripted crowd has no wallet and flies free, as it always did.
/// </para>
/// <para>
/// <b>A ticket is kept until it is flown.</b> A passenger that walks off the port, or asks for another city while it waits, keeps the one it bought; its next
/// boarding rides on it. A ticket belongs to the body that bought it, in memory: a logout or a restart forfeits an unflown one (its row stays in the ledger).
/// </para>
/// <para>
/// <b>A queued passenger without a ticket is charged when its shuttle is down</b> (review, 2026-11-03): Shuttle hands it to the next Ledger, which charges
/// it or takes it out of the queue. That covers a boarding the ask did not pay — asked while the world was paused, whose ticket was lost with a failed
/// commit — which would otherwise wait for a shuttle that never takes it. The ask's own charge is first, so an ask is never charged twice.
/// </para>
/// <para>
/// <b>The ticket set is written only by the Ledger and read only by Shuttle</b>, ordered by a declared resource (<c>ShuttleTickets</c>: Shuttle reads it,
/// Ledger writes it), so it needs no lock; the flights and the fares due that Shuttle's workers find are handed back through concurrent queues, read by the
/// next Ledger before any ask is charged.
/// </para>
/// </remarks>
public sealed partial class SimBridge
{
    /// <summary>How often the tickets of bodies no longer in the world are dropped, in seconds.</summary>
    private const int TicketPruneSeconds = 60;

    private readonly HashSet<EntityId> _tickets = [];
    private readonly ConcurrentQueue<EntityId> _flownOnTicket = new();
    private readonly ConcurrentQueue<EntityId> _faresDue = new();
    private readonly HashSet<EntityId> _faresDueSeen = [];
    private readonly List<EntityId> _faresDueCharged = [];

    // This tick's fares, paid in the Ledger's transaction: their tickets are taken back and their boardings cancelled if that transaction does not commit.
    private readonly List<(ClientCommand<BoardShuttle> Command, EntityId Body)> _faresApplied = [];
    private readonly HashSet<EntityId> _bodiesNow = [];
    private long _ticketsPrunedAt;

    private long _pendingFares;
    private long _fares;
    private long _faresRefused;
    private long _ticketFlights;

    /// <summary>Fares paid.</summary>
    public long FaresPaid => Interlocked.Read(ref _fares);

    /// <summary>Boardings refused for their fare: a wallet short of it, none open yet, or a transaction that did not commit.</summary>
    public long FaresRefused => Interlocked.Read(ref _faresRefused);

    /// <summary>Flights of a session's passenger, each on a ticket.</summary>
    public long TicketFlights => Interlocked.Read(ref _ticketFlights);

    /// <summary>Tickets bought and not flown yet.</summary>
    public int TicketsHeld => Volatile.Read(ref _ticketsHeld);

    private int _ticketsHeld;

    /// <summary>Whether a session's queued passenger may fly: it holds a ticket. Read by Shuttle's workers, while nothing writes the set.</summary>
    private bool HoldsTicket(EntityId body) => _tickets.Contains(body);

    /// <summary>A session's passenger queued without a ticket while its shuttle is down, from one of Shuttle's workers: the next Ledger charges it.</summary>
    private void FareDue(EntityId body) => _faresDue.Enqueue(body);

    /// <summary>Whether a Ledger run has fares due to charge: the Ledger opens its transaction for them.</summary>
    private bool FaresDue => !_faresDue.IsEmpty;

    /// <summary>A session's passenger flown on its ticket, from one of Shuttle's workers: the Ledger strikes the ticket off next.</summary>
    private void FlewOnTicket(EntityId body)
    {
        _flownOnTicket.Enqueue(body);
        Interlocked.Increment(ref _ticketFlights);
    }

    /// <summary>Strikes off the tickets flown since the last Ledger, and every minute those of bodies that left the world. Before any ask is charged.</summary>
    private void SettleTickets(long tick)
    {
        while (_flownOnTicket.TryDequeue(out var body))
        {
            _tickets.Remove(body);
        }

        if (_tickets.Count > 0 && tick - _ticketsPrunedAt >= (long)_config.TickRateHz * TicketPruneSeconds)
        {
            _ticketsPrunedAt = tick;
            TatooineReplication.FillCharacterBodies(_econBodies);
            _bodiesNow.Clear();
            foreach (var (_, body) in _econBodies)
            {
                _bodiesNow.Add(body);
            }

            _tickets.RemoveWhere(b => !_bodiesNow.Contains(b));
        }

        Volatile.Write(ref _ticketsHeld, _tickets.Count);
    }

    /// <summary>
    /// Charges the fare of a boarding <c>PlayerIntents</c> accepted this tick, or refuses it. An ask it refused, or that a later intent of the tick cancelled,
    /// has left its body not queued for that city, and is left alone.
    /// </summary>
    private void FareFromCommand(Transaction tx, Transaction tick0, SubscriptionsCommands subs, in ClientCommand<BoardShuttle> command, long worldTick)
    {
        if (!TatooineReplication.TryBodyOfSession(command.Session, out var body, out var character) || !tick0.TryOpen(body, out var player))
        {
            return;
        }

        var state = player.Read(Player.State);
        if (state.Activity != PlayerActivity.AwaitingShuttle || state.ShuttleDest != command.Value.DestCity || _tickets.Contains(body))
        {
            return;   // not queued by this ask, or riding on a ticket bought before
        }

        // The departure city plus one, so a fare from city 0 names something: the audit wants every fee to say what it paid for.
        OpenWallet(tx, character, worldTick);
        if (!_walletOf.ContainsKey((long)character.RawValue)
            || !Transfer(tx, (long)character.RawValue, EconomyLedger.World, TatooineData.ShuttleFare, TransferKind.Fare, state.ShuttleFrom + 1L, worldTick))
        {
            RefuseFare(tick0, subs, in command, body);
            return;
        }

        _tickets.Add(body);
        _faresApplied.Add((command, body));
        _walletsMoved.Add(body);
        _pendingFares++;
    }

    /// <summary>
    /// Charges the fares Shuttle found due — queued without a ticket while their shuttle is down — after the asks of the tick: one already charged by its ask
    /// holds a ticket and is left alone. A wallet short of it, or with none, takes the body out of the queue.
    /// </summary>
    private void ChargeFaresDue(Transaction tx, Transaction tick0, long worldTick)
    {
        _faresDueSeen.Clear();
        while (_faresDue.TryDequeue(out var body))
        {
            if (!_faresDueSeen.Add(body) || _tickets.Contains(body) || !tick0.TryOpen(body, out var player))
            {
                continue;
            }

            var state = player.Read(Player.State);
            if (state.Activity != PlayerActivity.AwaitingShuttle || !_econCharacterOf.TryGetValue(body, out var character))
            {
                continue;
            }

            OpenWallet(tx, character, worldTick);
            if (!_walletOf.ContainsKey((long)character.RawValue)
                || !Transfer(tx, (long)character.RawValue, EconomyLedger.World, TatooineData.ShuttleFare, TransferKind.Fare, state.ShuttleFrom + 1L, worldTick))
            {
                CancelBoarding(tick0, body);
                Interlocked.Increment(ref _faresRefused);
                continue;
            }

            _tickets.Add(body);
            _faresDueCharged.Add(body);
            _walletsMoved.Add(body);
            _pendingFares++;
        }
    }

    /// <summary>
    /// The Ledger's transaction ended: its fares are counted, or their tickets taken back — the asks refused and their boardings cancelled; a fare found
    /// due is charged again by the next shuttle down.
    /// </summary>
    private void EndFares(Transaction tick0, SubscriptionsCommands subs, bool committed)
    {
        if (committed)
        {
            Interlocked.Add(ref _fares, _pendingFares);
        }
        else
        {
            foreach (var (command, body) in _faresApplied)
            {
                _tickets.Remove(body);
                RefuseFare(tick0, subs, in command, body);
            }

            foreach (var body in _faresDueCharged)
            {
                _tickets.Remove(body);
            }
        }

        _faresApplied.Clear();
        _faresDueCharged.Clear();
        _pendingFares = 0;
        Volatile.Write(ref _ticketsHeld, _tickets.Count);
    }

    /// <summary>Takes the body out of the queue, on the tick's own transaction, and refuses the ask.</summary>
    private void RefuseFare(Transaction tick0, SubscriptionsCommands subs, in ClientCommand<BoardShuttle> command, EntityId body)
    {
        CancelBoarding(tick0, body);
        subs.Reject(command, TatooineReplication.ShuttleFareUnpaid);
        Interlocked.Increment(ref _faresRefused);
    }

    /// <summary>Takes a queued body out of the queue, on the tick's own transaction: its client sees it idle where it stood.</summary>
    private static void CancelBoarding(Transaction tick0, EntityId body)
    {
        if (tick0.TryOpenMut(body, out var player))
        {
            var state = player.Read(Player.State);
            if (state.Activity == PlayerActivity.AwaitingShuttle)
            {
                state.Activity = PlayerActivity.Idle;
                state.ActivityTicks = 0;
                state.ShuttleFrom = 0;
                state.ShuttleDest = 0;
                player.Set(Player.State, state);
                TatooineReplication.Replicate(in player);
            }
        }
    }
}
