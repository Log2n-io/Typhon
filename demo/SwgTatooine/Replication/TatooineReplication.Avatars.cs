using System;
using System.Collections.Generic;
using SwgTatooine.Accounts;

namespace SwgTatooine;

/// <summary>
/// A5: a login spawns the account's avatar where its character left off, and a logout saves and destroys it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This replaces possession, and the difference is the whole requirement.</b> <c>PossessPlayers</c> handed each session the
/// first free player out of the pooled crowd, so logging out and back in landed on whatever body happened to be unclaimed — which
/// is exactly the one thing "log in and be where you were" cannot tolerate. An avatar is spawned for the session, at the
/// character's saved position and realm, and destroyed when the session goes.
/// </para>
/// <para>
/// <b>The pooled crowd is untouched.</b> Those 320 x <c>--pop</c> players stay <c>ControllerKind.Scripted</c> and keep being
/// driven by <c>PlayerThink</c>; nothing here claims one, ever. An avatar is <i>additional</i>, one per real session, which is
/// what acceptance criterion A-8 measures.
/// </para>
/// <para>
/// <b>In the serial session system and nowhere else</b> (see <c>SessionTick</c>'s remarks for why that system is serial). Every
/// spawn, destroy, character read and character write below runs on the tick's own transaction: no side transaction, so there is
/// nothing for <c>EW-01</c> to catch, and the whole login is one atomic unit — the character is read or created, the avatar is
/// spawned and the session is given control in one commit, or none of it happens.
/// </para>
/// <para>
/// <b>Release runs before bind, and the order is load-bearing.</b> A client that drops and reconnects arrives as a NEW session
/// while the old one may still be closing, and the account guard below refuses a second session for one account. Releasing first
/// means the old session's hold is gone by the time the new one is considered, so a reconnect in the same tick as the disconnect
/// binds instead of being refused.
/// </para>
/// </remarks>
public static partial class TatooineReplication
{
    /// <summary>
    /// The close code a <c>player</c> session is refused with when its account is already playing.
    /// </summary>
    /// <remarks>
    /// <b>Refused rather than kicking the older session, which is a decision and not the nicer behaviour.</b> Kicking the
    /// incumbent is what an MMO does, and it needs a tick of waiting: the old session must release its avatar before the new one
    /// can bind, so the new client would sit unbound for a frame and the code would have to remember why. Refusing is one tick,
    /// deterministic, and says something true to the client. The hand-over is worth building when a real person is locked out by
    /// their own dead connection, and the grace period makes that a real complaint rather than a theoretical one.
    /// </remarks>
    public const ushort AccountAlreadyPlayingCloseCode = 4103;

    /// <summary>Where a character with no saved position starts, and the realm it starts in.</summary>
    /// <remarks>
    /// Set by <see cref="ConfigureAvatars"/> from the map rather than defaulted to the origin, which on this planet is open desert
    /// a long way from anything: a first login should arrive somewhere there is something to see.
    /// </remarks>
    private static Point3F DefaultSpawn;

    private static ushort DefaultSpawnRealm;

    /// <summary>
    /// The towns new characters are distributed across, with the cumulative weight that selects one; empty spawns everybody on
    /// <see cref="DefaultSpawn"/>.
    /// </summary>
    private static SpawnArea[] SpawnAreas = [];

    /// <summary>One town new characters may arrive in: where it is, how wide, and what share of arrivals it takes.</summary>
    /// <param name="X">The centre's world x.</param>
    /// <param name="Z">The centre's world z.</param>
    /// <param name="RadiusM">The disc arrivals are spread over, metres.</param>
    /// <param name="Weight">
    /// The share of arrivals, relative to the other areas — normalised by <see cref="ConfigureAvatars"/>, so the map's own figures can
    /// be passed as they are. Replaced in place by the CUMULATIVE share once normalised.
    /// </param>
    /// <remarks>
    /// <b>Several towns rather than one, because one town is a stampede at any interesting population.</b> Ten thousand arrivals in the
    /// capital's 456 m disc put roughly 1 770 bodies inside every session's 192 m sphere, which at 50 Hz is 124 records a frame and
    /// 54 GB of traffic in ninety seconds — the fleet's own process died of it while the server stayed inside its tick budget. Tatooine
    /// has seven towns, up to 8.8 km apart, and <c>TatooineData.Cities</c> has carried a <c>playerWeight</c> per town summing to 1.00
    /// since it was written. Spreading arrivals across them is using data that was already there.
    /// </remarks>
    public readonly record struct SpawnArea(float X, float Z, double RadiusM, double Weight);

    /// <summary>Ground height at a point, so a scattered spawn lands ON the terrain rather than at the capital's height.</summary>
    private static Func<float, float, float> SpawnGroundAt;

    /// <summary>
    /// Refuses to start a server that cannot host characters.
    /// </summary>
    /// <exception cref="InvalidOperationException"><see cref="ConfigureAvatars"/> was never called.</exception>
    /// <remarks>
    /// <b>This is where <see cref="ConfigureAvatars"/>' claim that "a server that forgot this refuses to start" is actually made true.</b>
    /// The check used to live inside the bind, on the tick, where the demo's <c>AbortTickAndStop</c> policy turned it into a server stop
    /// triggered by whichever client happened to log in first. Start-up is both earlier and cheaper, and it fails the person who can fix
    /// it rather than the people who were playing.
    /// </remarks>
    public static void ThrowIfAvatarsNotConfigured()
    {
        if (!AvatarsConfigured)
        {
            throw new InvalidOperationException(
                "TatooineReplication.ConfigureAvatars was not called, so a login would have no account index and no spawn point. "
                + "Call it before starting the runtime.");
        }
    }

    /// <summary>The index over <see cref="Character.Account"/>, resolved once. See <see cref="ConfigureAvatars"/>.</summary>
    private static IndexRef CharacterByAccount;

    private static bool AvatarsConfigured;

    /// <summary>
    /// How often a live avatar's position is written back to its character, in ticks (A6). Zero disables the cadence.
    /// </summary>
    /// <remarks>
    /// <b>This number IS acceptance criterion A-5.</b> "Position loss on a <c>kill -9</c> is at most the cadence, stated as a
    /// number" — so the number has to exist somewhere a reader can find, and the criterion is then arithmetic rather than a hope.
    /// Set from <see cref="ConfigureAvatars"/> out of the tick rate, so a server at 10 Hz and one at 50 Hz save at the same
    /// wall-clock interval rather than at the same tick count.
    /// </remarks>
    private static int SaveIntervalTicks;

    /// <summary>The wall-clock interval the cadence was configured for, for the banner and the tests.</summary>
    public static double SaveIntervalSeconds { get; private set; }

    /// <summary>Session value to the account its token named, filled on <c>Opened</c> and drained when the avatar binds.</summary>
    /// <remarks>
    /// <b>Captured at the event rather than looked up later, because <c>AppData</c> exists only on the event.</b> The admission
    /// hook proved the token and attached a <see cref="PlayerIdentity"/>; <c>SessionEvent.AppData</c> is the one place that reaches
    /// the tick, and <c>WalkSessions</c> — which is what notices an unbound session — sees only the session id.
    /// </remarks>
    private static readonly Dictionary<uint, EntityId> AccountOfSession = [];

    /// <summary>Session value to the character its avatar belongs to, so a logout knows what to save.</summary>
    private static readonly Dictionary<uint, EntityId> CharacterOfSession = [];

    /// <summary>Account raw value to the session currently playing it, so one account cannot be in the world twice.</summary>
    /// <remarks>
    /// Two avatars for one account would be two bodies with one wallet, and whichever logged out last would overwrite the other's
    /// position — so "where you were" would depend on the order two of your own clients closed.
    /// </remarks>
    private static readonly Dictionary<ulong, uint> SessionOfAccount = [];

    /// <summary>
    /// The avatar of every session whose link died and whose client may still come back: the old session's value to the body it left standing (A13).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the whole of what resume costs the application, and it is held on the engine's promise rather than on a timer of our own.</b> The engine
    /// closes a link-dead session with <c>Resumable</c> set, keeps its record for <c>ResumeGraceMs</c>, and then delivers exactly one of
    /// <c>Opened{ResumedFrom}</c> or <c>ResumeExpired</c> (SUB-12). So an entry here is removed by one of those two events and by nothing else — there is no
    /// deadline in this file, and a hold cannot leak unless the engine breaks that rule.
    /// </para>
    /// <para>
    /// <b>The character and the account hold stay where they were.</b> <see cref="CharacterOfSession"/> still names the character under the OLD session's
    /// value, and <see cref="SessionOfAccount"/> still points at it, which is what keeps a second client from walking in beside a body that is still
    /// standing. A resume moves both onto the new session; an expiry drops both with the avatar.
    /// </para>
    /// <para>
    /// <b>The body is stopped where it stood</b> (<see cref="HoldForResume"/>). Left alone it would keep walking to the destination its last intent named,
    /// with nobody able to change it — measured 2.8 m past where its player left it in the first run of these cases, and unbounded on a long walk, which on
    /// this map can carry a body through a portal into another realm.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<uint, ResumeHold> ResumingPlayer = [];

    /// <summary>One held avatar: the body left standing, and the account it belongs to.</summary>
    /// <param name="Avatar">The body.</param>
    /// <param name="AccountRaw">The account's raw value, as <see cref="SessionOfAccount"/> keys it.</param>
    /// <remarks>
    /// <b>The account is captured at the hold, by one scan, because afterwards nothing can answer for it.</b> <see cref="AccountOfSession"/> is drained when
    /// an avatar binds, so a held session has no entry there, and <see cref="SessionOfAccount"/> only goes the other way. Every path that ends a hold has to
    /// release the account — a resume moves it, an expiry and a vanished body drop it — and a path that cannot name it either strands the account (its own
    /// player refused with 4103 for ever) or drops it while the body is still being driven (two clients, one character). Both were reachable before this
    /// field existed; see the review notes on A13 in 14-game-programme.md.
    /// </remarks>
    private readonly record struct ResumeHold(EntityId Avatar, ulong AccountRaw);

    /// <summary>Holds whose grace elapsed, queued by the session-event walk for the transaction in <see cref="RetireAvatars"/>.</summary>
    /// <remarks>
    /// A queue rather than a destroy at the event, because the walk runs ahead of the tick's player accessor: saving a position needs the avatar's placement
    /// and the character's component, and both come from accessors <see cref="BindAvatars"/> rents once for the whole tick.
    /// </remarks>
    private static readonly List<uint> ResumesToRetire = [];

    private static long _avatarsSpawned;
    private static long _avatarsDestroyed;
    private static long _charactersCreated;
    private static long _loginsRefusedDuplicate;
    private static long _loginsRefusedAnonymous;
    private static long _loginsRefusedUnconfigured;
    private static long _locationsSaved;
    private static long _resumesHeld;
    private static long _resumesRebound;
    private static long _resumeHoldsExpired;
    private static long _resumeHoldsTakenOver;
    private static long _resumeExpiriesWithoutHold;

    /// <summary>Avatars held for a link-dead session's possible return (A13).</summary>
    public static long ResumesHeld => System.Threading.Interlocked.Read(ref _resumesHeld);

    /// <summary>Avatars handed back to a client that came back within the grace, without a spawn or a login.</summary>
    public static long ResumesRebound => System.Threading.Interlocked.Read(ref _resumesRebound);

    /// <summary>Held avatars retired because the grace elapsed with nobody coming back.</summary>
    public static long ResumeHoldsExpired => System.Threading.Interlocked.Read(ref _resumeHoldsExpired);

    /// <summary>Held avatars retired because their own account logged in fresh instead of resuming.</summary>
    public static long ResumeHoldsTakenOver => System.Threading.Interlocked.Read(ref _resumeHoldsTakenOver);

    /// <summary>`ResumeExpired` events that arrived for a session this side was holding nothing for.</summary>
    /// <remarks>
    /// <b>Zero is the claim; a non-zero value is a disagreement between the engine and this demo about who is holding what.</b> The engine promises
    /// exactly one of <c>Opened{ResumedFrom}</c> or <c>ResumeExpired</c> per resumable close (SUB-12), so an expiry with no hold behind it means the hold
    /// was dropped by some other path first — and the avatar it was keeping standing is then nobody's.
    /// <para>
    /// It exists because the event was being discarded silently. A god session's close is resumable too and queueing its expiry would defeat
    /// <c>BindAvatars</c>' early-out for a no-op, so the handler tests for a hold and drops the event when there is none — correct for the god case and
    /// indistinguishable from the leak. 245 avatars were once left standing with no session long past their grace and this was the number that would have
    /// said whether the expiries never arrived or arrived and were thrown away; it did not exist, so the question could not be answered from the box.
    /// </para>
    /// </remarks>
    public static long ResumeExpiriesWithoutHold => System.Threading.Interlocked.Read(ref _resumeExpiriesWithoutHold);

    /// <summary>
    /// Records a <c>ResumeExpired</c> that arrived for a session this side was holding nothing for, when that session is one it should have been
    /// (<see cref="ResumeExpiriesWithoutHold"/>).
    /// </summary>
    /// <param name="session">The session whose expiry arrived.</param>
    /// <remarks>
    /// <b>The predicate is the whole value of the counter, because three different things reach here legitimately.</b> A god session's close is resumable
    /// and it never had an avatar; a session whose own account logged in fresh had its hold retired by the takeover; and a session whose hold was retired
    /// for any other reason is in the same position. Counting all of those would ship a number whose zero means nothing, which is no better than the
    /// silent drop it replaces.
    /// <para>
    /// So the test is <see cref="CharacterOfSession"/>: every one of the benign cases has left it — <c>ForgetHold</c> removes the character with the hold,
    /// and a god session was never in it — while a PLAYER session this side still knows the character of, with no hold, is a body standing for nobody.
    /// That is the one case where the engine's "exactly one of Opened{ResumedFrom} or ResumeExpired" (SUB-12) and this demo's own bookkeeping disagree.
    /// </para>
    /// </remarks>
    internal static void NoteResumeExpiryWithoutHold(SessionId session)
    {
        if (CharacterOfSession.ContainsKey(session.Value))
        {
            System.Threading.Interlocked.Increment(ref _resumeExpiriesWithoutHold);
        }
    }

    /// <summary>Avatars standing for a client that is not connected right now.</summary>
    /// <remarks>
    /// <b>The map itself, not an interlocked counter like its neighbours, and the difference is the thread.</b> Those count events and are incremented from
    /// whichever thread produced one; a hold is taken and released by the serial session system alone, so this is a tick-side read. A caller outside the tick
    /// — a stats endpoint, a check between ticks — sees a consistent count because the tick is not running, which is also when every one of them asks.
    /// </remarks>
    public static int ResumeHoldsStanding => ResumingPlayer.Count;

    /// <summary>Avatars spawned by a login since the last reset.</summary>
    public static long AvatarsSpawned => System.Threading.Interlocked.Read(ref _avatarsSpawned);

    /// <summary>Avatars destroyed by a logout since the last reset.</summary>
    public static long AvatarsDestroyed => System.Threading.Interlocked.Read(ref _avatarsDestroyed);

    /// <summary>Characters created by a first login since the last reset.</summary>
    public static long CharactersCreated => System.Threading.Interlocked.Read(ref _charactersCreated);

    /// <summary>Sessions refused because their account was already playing.</summary>
    public static long LoginsRefusedDuplicate => System.Threading.Interlocked.Read(ref _loginsRefusedDuplicate);

    /// <summary>Sessions refused because they reached the bind with no identity at all.</summary>
    public static long LoginsRefusedAnonymous => System.Threading.Interlocked.Read(ref _loginsRefusedAnonymous);

    /// <summary>Logins refused because this process never called <see cref="ConfigureAvatars"/>.</summary>
    public static long LoginsRefusedUnconfigured => System.Threading.Interlocked.Read(ref _loginsRefusedUnconfigured);

    /// <summary>Character positions written back since the last reset — logouts, plus A6's cadence when it lands.</summary>
    public static long LocationsSaved => System.Threading.Interlocked.Read(ref _locationsSaved);

    /// <summary>
    /// Resolves what binding an avatar needs from the database and the map, once.
    /// </summary>
    /// <param name="dbe">The engine, for the account index's handle.</param>
    /// <param name="spawn">Where a brand-new character starts.</param>
    /// <param name="realm">The realm a brand-new character starts in.</param>
    /// <param name="tickRateHz">The server's tick rate, which turns the save interval into a tick count.</param>
    /// <param name="saveIntervalSeconds">
    /// How often a live avatar's position is written back (A6). Zero disables the cadence, which means a <c>kill -9</c> costs the
    /// whole session's walking rather than this many seconds of it.
    /// </param>
    /// <remarks>
    /// <b>Required rather than defaulted, like <see cref="ConfigureIntents"/>.</b> A server that forgot this refuses to start
    /// instead of spawning everybody at the origin — which on this planet is empty desert, and is the kind of wrong that looks
    /// like a bug in the client.
    /// </remarks>
    public static void ConfigureAvatars(
        DatabaseEngine dbe,
        Point3F spawn,
        ushort realm,
        int tickRateHz,
        double saveIntervalSeconds = DefaultSaveIntervalSeconds,
        SpawnArea[] spawnAreas = null,
        Func<float, float, float> groundAt = null)
    {
        ArgumentNullException.ThrowIfNull(dbe);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tickRateHz);
        ArgumentOutOfRangeException.ThrowIfNegative(saveIntervalSeconds);

        // Refused rather than ignored, and refused rather than falling back to one height for every town. Towns are kilometres apart on
        // a dune field: arrivals that all sat at the capital's altitude would be underground in one town and in the air in the next,
        // which reads as a terrain bug a long way from here.
        if (spawnAreas is { Length: > 0 } && groundAt == null)
        {
            throw new ArgumentNullException(nameof(groundAt), "spawn areas need a ground probe, or arrivals would sit at one town's height in all of them.");
        }

        // Resolved once: an IndexRef captures a layout version and is reusable at no cost, and the alternative is resolving it per
        // login inside the tick, where the expression-tree walk GetIndexRef does would be the most expensive thing a login did.
        CharacterByAccount = dbe.GetIndexRef<Character, EntityLink<AccountArch>>(c => c.Account);
        DefaultSpawn = spawn;
        DefaultSpawnRealm = realm;
        SpawnGroundAt = groundAt;

        // Normalised to a CUMULATIVE weight here rather than at every login: the selection is a binary-search-free walk over seven
        // entries, and doing the division once means a caller can pass the map's own weights without being asked to make them sum.
        if (spawnAreas is { Length: > 0 })
        {
            var total = 0d;
            foreach (var area in spawnAreas)
            {
                total += Math.Max(0d, area.Weight);
            }

            if (total <= 0d)
            {
                throw new ArgumentException("the spawn areas carry no weight, so no town could ever be chosen.", nameof(spawnAreas));
            }

            var cumulative = new SpawnArea[spawnAreas.Length];
            var running = 0d;
            for (var i = 0; i < spawnAreas.Length; i++)
            {
                running += Math.Max(0d, spawnAreas[i].Weight) / total;
                cumulative[i] = spawnAreas[i] with { Weight = i == spawnAreas.Length - 1 ? 1d : running };
            }

            SpawnAreas = cumulative;
        }
        else
        {
            SpawnAreas = [];
        }

        // Rounded UP to one tick, so a cadence shorter than a tick saves every tick rather than every tick being skipped by an
        // interval of zero — which `% 0` would make a divide-by-zero rather than a fast path.
        //
        // Clamped as a DOUBLE before the cast, because a double-to-int conversion out of range is unspecified in an unchecked
        // context: `--avatar-save-interval 1e9` produced int.MinValue, which Math.Max(1, …) then turned into 1, so asking for a
        // save every thirty years saved every tick — the exact opposite, silently. int.MaxValue ticks is ~500 days at 50 Hz, which
        // is "never" for any purpose this knob has.
        var intervalTicks = Math.Round(saveIntervalSeconds * tickRateHz);
        SaveIntervalTicks = saveIntervalSeconds <= 0d ? 0 : (int)Math.Clamp(intervalTicks, 1d, int.MaxValue);
        SaveIntervalSeconds = SaveIntervalTicks == 0 ? 0d : (double)SaveIntervalTicks / tickRateHz;
        AvatarsConfigured = true;
    }

    /// <summary>
    /// How much walking a hard kill costs, by default.
    /// </summary>
    /// <remarks>
    /// Ten seconds, which at the run speed is about seventy metres — annoying and recognisable, rather than a lost session. The
    /// cost is one component write per avatar per interval, and avatars are one per human, so at any population a server can
    /// actually serve this is unmeasurable beside what the pooled crowd does every tick.
    /// </remarks>
    public const double DefaultSaveIntervalSeconds = 10d;

    /// <summary>
    /// Notes the account an opening <c>player</c> session belongs to.
    /// </summary>
    /// <param name="session">The session that opened.</param>
    /// <param name="appData">What the admission hook attached — a <see cref="PlayerIdentity"/> for a player, <see langword="null"/> for a god camera.</param>
    /// <remarks>
    /// Called from the <c>Opened</c> branch of the session-event walk. A god session carries no identity and none is recorded, which
    /// is what keeps it out of <see cref="BindAvatars"/> entirely.
    /// </remarks>
    private static void NoteOpenedSession(SessionId session, object appData)
    {
        if (appData is PlayerIdentity identity)
        {
            AccountOfSession[session.Value] = identity.Account;
        }
    }

    /// <summary>
    /// Keeps a link-dead session's avatar standing for the grace instead of retiring it (A13).
    /// </summary>
    /// <param name="tx">The tick's transaction, or <see langword="null"/> — then the body keeps its last intent until the next tick stops it.</param>
    /// <param name="session">The session that closed.</param>
    /// <returns><see langword="true"/> when a hold was taken, so the caller knows the avatar is not being retired.</returns>
    /// <remarks>
    /// Moving the entry out of <see cref="BoundPlayer"/> is what spares it: <see cref="RetireAvatars"/> destroys exactly the bound avatars whose session is no
    /// longer open, and a held one is no longer bound. Everything else about it is unchanged — it is still replicated to everyone else and still in the spatial
    /// index, which is the point. The player's body is still in the world; it just has nobody driving it.
    /// </remarks>
    private static bool HoldForResume(Transaction tx, SessionId session)
    {
        if (!BoundPlayer.TryGetValue(session.Value, out var avatar))
        {
            return false;
        }

        BoundPlayer.Remove(session.Value);
        ResumingPlayer[session.Value] = new ResumeHold(avatar, AccountHeldBy(session.Value));
        StopHeldAvatar(tx, avatar);
        System.Threading.Interlocked.Increment(ref _resumesHeld);
        return true;
    }

    /// <summary>Whether a session's avatar is being held for its client's return (A13).</summary>
    /// <param name="session">The session that closed.</param>
    /// <returns><see langword="true"/> when a hold stands for it.</returns>
    internal static bool HoldsResume(SessionId session) => ResumingPlayer.ContainsKey(session.Value);

    /// <summary>The account a session holds, or zero.</summary>
    /// <param name="sessionValue">The session.</param>
    /// <returns>The account's raw value, or <c>0</c> when it holds none.</returns>
    /// <remarks>
    /// A scan, and it is the right shape: <see cref="SessionOfAccount"/> is keyed by account because that is the question a login asks, and this is the one
    /// moment the inverse is wanted — once per dropped link, over as many entries as there are players on the server.
    /// </remarks>
    private static ulong AccountHeldBy(uint sessionValue)
    {
        foreach (var (accountRaw, holder) in SessionOfAccount)
        {
            if (holder == sessionValue)
            {
                return accountRaw;
            }
        }

        return 0;
    }

    /// <summary>
    /// Drops a hold whose body is no longer there, so that neither the hold nor its account outlives it.
    /// </summary>
    /// <param name="sessionValue">The held session's value.</param>
    /// <remarks>
    /// <b>Nothing is saved or destroyed, because there is nothing left to save or destroy</b> — this is the path where the avatar has already gone from under
    /// the hold. What matters is that the hold goes with it: the engine spends its record on whichever of the two events it delivered, so an entry left here
    /// would stay in a process-lifetime static for ever, and the account it pins would refuse its own player's next login with 4103 for just as long.
    /// </remarks>
    private static void ForgetHold(uint sessionValue)
    {
        if (!ResumingPlayer.Remove(sessionValue, out var hold))
        {
            return;
        }

        CharacterOfSession.Remove(sessionValue);
        AccountOfSession.Remove(sessionValue);
        if (hold.AccountRaw != 0 && SessionOfAccount.TryGetValue(hold.AccountRaw, out var holder) && holder == sessionValue)
        {
            SessionOfAccount.Remove(hold.AccountRaw);
        }
    }

    /// <summary>
    /// Stops a held avatar where it stands, because the client that was steering it is gone.
    /// </summary>
    /// <param name="tx">The tick's transaction, or <see langword="null"/> to skip it.</param>
    /// <param name="avatar">The held avatar.</param>
    /// <remarks>
    /// <b>The intent outlives the session, and that is the bug this closes.</b> <c>PlayerMotion</c> holds a destination, and <c>PlayerMoveTick</c> keeps
    /// walking towards it every tick whether or not anybody is connected — so a player who dropped mid-walk would be found somewhere they never chose, having
    /// run on at full speed with nobody able to stop them. The destination is set to where the body is and the velocity to zero, which is the same stop a
    /// <c>MoveDir</c> with no direction produces; it is written here rather than left to the intent path because there is no longer a session to send one.
    /// <para>
    /// In the session system's own transaction, in the <c>Spawn</c> phase, which is where <c>ApplyIntents</c> writes this component too — so this is one more
    /// write on a path that already owns it, not a new writer the DAG would have to order.
    /// </para>
    /// </remarks>
    private static void StopHeldAvatar(Transaction tx, EntityId avatar)
    {
        if (tx == null || !tx.For<Player>().TryOpenMut(avatar, out var body))
        {
            return;
        }

        var place = body.Read(Player.Bounds);
        var move = body.Read(Player.Move);
        move.DestX = place.X;
        move.DestZ = place.Z;
        move.VelX = 0f;
        move.VelZ = 0f;
        body.Set(Player.Move, move);
    }

    /// <summary>
    /// Hands a session the avatar a held one left standing: no spawn, no character read, no position to restore (A13).
    /// </summary>
    /// <param name="subs">The commands this tick stages.</param>
    /// <param name="tx">The tick's transaction.</param>
    /// <param name="session">The session that gets the body.</param>
    /// <param name="heldBy">The session whose hold it is — a resume's <c>Opened.ResumedFrom</c>, or the holder a fresh login displaces.</param>
    /// <returns><see langword="false"/> when there was no hold, so the caller lets the ordinary login run.</returns>
    /// <remarks>
    /// <para>
    /// <b>Why there is nothing to read.</b> A login exists to find out where the character was; this does not have to ask, because the body never left. Its
    /// position is wherever it is standing, its realm is the realm it is in, and the one thing missing is a session to drive it — which <c>Control</c>
    /// supplies, and which with the <c>AroundControlled</c> profile also puts the new session in the avatar's realm and centres its sphere there.
    /// </para>
    /// <para>
    /// <b><c>PlayerSession.Controller</c> has to move too, and forgetting it is not a cosmetic bug.</b> That field is the demo's own record of who drives
    /// this body: <c>TryOpenControlled</c> checks it before applying any intent, and <c>ControlledBy</c> finds an avatar by it. An avatar still naming the
    /// session that died would have been handed to the client and then refused every command it sent — which is exactly what the first run of
    /// <c>ResumeAvatarChecks</c> saw.
    /// </para>
    /// <para>
    /// A hold that is not there is not an error: the grace may have elapsed. The client is then logged in normally, at its character's last saved position,
    /// which is the same answer it would have had without resume.
    /// </para>
    /// </remarks>
    private static bool TryRebindHeldAvatar(SubscriptionsCommands subs, Transaction tx, SessionId session, SessionId heldBy)
    {
        if (!ResumingPlayer.TryGetValue(heldBy.Value, out var hold))
        {
            return false;
        }

        if (tx == null || !tx.For<Player>().TryOpenMut(hold.Avatar, out var body))
        {
            // The body has gone from under the hold — a realm torn down over it, or anything that destroys a player — and the engine has already spent its
            // record on the event that brought us here. Forgotten rather than left: an entry nothing can end is a leak in a process-lifetime static, and the
            // account it pins would refuse this very player for the rest of the run. The caller then logs the client in normally.
            ForgetHold(heldBy.Value);
            return false;
        }

        var owner = body.Read(Player.Session);
        owner.Controller = session.Value;
        body.Set(Player.Session, owner);

        ResumingPlayer.Remove(heldBy.Value);
        BoundPlayer[session.Value] = hold.Avatar;

        if (CharacterOfSession.Remove(heldBy.Value, out var character))
        {
            CharacterOfSession[session.Value] = character;
        }

        // The account moves off the held session and onto this one, taken from the HOLD rather than from what this session presented. Reading it from
        // `AccountOfSession` instead would leave the account naming a session that no longer holds anything whenever the new session carried no identity —
        // and `DropAccountHolds` would then free an account whose body is being driven, which is two clients on one character.
        AccountOfSession.Remove(session.Value);
        if (hold.AccountRaw != 0)
        {
            SessionOfAccount[hold.AccountRaw] = session.Value;
        }

        subs.Session(session).Control(hold.Avatar);
        System.Threading.Interlocked.Increment(ref _possessions);
        return true;
    }

    /// <summary>
    /// Spawns an avatar for every session that logged in and does not have one, and retires the avatars of sessions that have gone.
    /// </summary>
    /// <param name="tick">The tick context.</param>
    /// <remarks>
    /// <para>
    /// <b>One transaction — the tick's — for the whole login.</b> Read the character through its account index, create it if this is
    /// a first login, spawn the avatar at its saved position and realm, and hand the session control. All of it commits together, so
    /// there is no state where a character exists with no avatar or a session controls an entity that was never committed.
    /// </para>
    /// <para>
    /// <b><c>Control</c> alone puts the session in the avatar's realm; no <c>Enter</c> is needed.</b> The player profile is
    /// <c>AroundControlled</c>, and as <c>Spectate</c>'s handling says, "the profile carries the anchor and <c>Control</c> names the
    /// entity; together they make the session's realm and centre the subject's". A login into an interior therefore arrives inside
    /// it, and an explicit <c>Enter</c> would be a second statement of the same fact that could disagree with the first.
    /// </para>
    /// </remarks>
    private static void BindAvatars(TickContext tick)
    {
        var subs = tick.Subscriptions;
        var tx = tick.Transaction;
        if (subs == null || tx == null)
        {
            return;
        }

        // A6's cadence runs on every tick, so it is ahead of the early return below: that return is for "nothing opened and nothing
        // closed", which is almost every tick and is exactly when a live avatar's position still needs saving.
        SaveAvatarsOnCadence(tick, subs, tx);

        // `Unbound` and `Seen` were filled by WalkSessions. Nothing to bind and nothing to retire is the common case by far — a
        // server spends almost every tick with a stable set of clients — and worth detecting, because tx.For<Player>() creates an
        // EntityMap accessor and pre-warms the component table.
        if (Unbound.Count == 0 && BoundPlayer.Count == Seen.Count && ResumesToRetire.Count == 0)
        {
            return;
        }

        // NOT disposed: the accessor comes from the TICK's transaction, which owns it and releases it. Disposing one taken from a
        // transaction this method did not create tears down the cached EntityMap and chunk accessors mid-tick, which stops later
        // systems reading. Same for the character accessor below.
        var players = tx.For<Player>();

        // Retire first — see the class remarks: a reconnect arriving in the same tick as its own disconnect must find its account free.
        RetireAvatars(tx, players);

        for (var i = 0; i < Unbound.Count; i++)
        {
            BindOneAvatar(subs, tx, Unbound[i]);
        }
    }

    /// <summary>Spawns one session's avatar, or refuses the session.</summary>
    /// <param name="subs">The commands this tick stages.</param>
    /// <param name="tx">The tick's transaction.</param>
    /// <param name="session">The session to bind.</param>
    private static void BindOneAvatar(SubscriptionsCommands subs, Transaction tx, SessionId session)
    {
        // A resumed session was bound by the session-event walk, which runs after WalkSessions put it in `Unbound` and cannot take it back out — the list is
        // what WalkSessions saw. Without this it would be logged in a second time and spawn a second avatar beside the one it had just been handed.
        if (BoundPlayer.ContainsKey(session.Value))
        {
            return;
        }

        if (!AccountOfSession.TryGetValue(session.Value, out var account))
        {
            // Unreachable through Admit, which refuses a player session with no valid token (A3) — so this is the guard that says so
            // rather than leaving the session connected and permanently blind. Kicked rather than ignored: a session with no avatar
            // receives nothing and would look like a server that had stopped.
            System.Threading.Interlocked.Increment(ref _loginsRefusedAnonymous);
            subs.Session(session).Kick(NotAuthenticatedCloseCode, "no identity reached the tick — log in again");
            return;
        }

        if (SessionOfAccount.TryGetValue(account.RawValue, out var holder) && holder != session.Value)
        {
            // A HELD account is not a playing one (A13): the body is standing there because the link died, and this is that same account arriving without a
            // resume token — a reopened browser, a second device, a client that lost the token. Refusing it would lock the player out of their own character
            // for the whole grace, which is the opposite of what the grace is for.
            //
            // <b>It is handed the body rather than a fresh one at the saved position</b>, which is the same move a resume makes and for a stronger reason:
            // this client proved the ACCOUNT, where a resume proves only that it held a token. Retiring and respawning would also have had to read back a
            // position written moments earlier in this same transaction, and spawn a second entity for one player in one tick for no gain.
            // Captured BEFORE the attempt, because a rebind that fails forgets the hold — so testing the map again below would read false and refuse this
            // client for an account nobody is playing.
            var held = ResumingPlayer.ContainsKey(holder);
            if (held && TryRebindHeldAvatar(subs, tx, session, SessionId.FromValue(holder)))
            {
                System.Threading.Interlocked.Increment(ref _resumeHoldsTakenOver);
                return;
            }

            // Refused only for an account a LIVE session is playing. A hold whose body had gone was forgotten just now, account and all, so this login falls
            // through and spawns a body — which is the right answer for a player whose avatar vanished from under them.
            if (!held)
            {
                System.Threading.Interlocked.Increment(ref _loginsRefusedDuplicate);
                subs.Session(session).Kick(AccountAlreadyPlayingCloseCode, "that account is already playing");

                // Safe to forget the identity on the way out, and it is worth saying why, because the kick is STAGED rather than
                // immediate. The refused session cannot be reconsidered on a later tick: `SubscriptionsIngressExecSystem.BeginTick`
                // applies last tick's requests — turning this Kick into a real close — BEFORE it publishes the open set, so the session is
                // gone from `OpenSessions` by the time `WalkSessions` next runs and never re-enters `Unbound`.
                AccountOfSession.Remove(session.Value);
                return;
            }
        }

        if (!AvatarsConfigured)
        {
            // A programmer error, but NOT a throw: this runs inside the session system, and with SystemExceptionPolicy.AbortTickAndStop
            // a throw here stopped the whole server on somebody's login — turning one misconfigured host into an outage for every
            // session already playing. The configuration is checked where ConfigureAvatars' own doc says it is, at start-up
            // (ThrowIfAvatarsNotConfigured, called from StartReplication), so reaching this is a host that drove replication without it.
            // The session is kicked, which tells that one client the truth and leaves the tick alive.
            System.Threading.Interlocked.Increment(ref _loginsRefusedUnconfigured);
            subs.Session(session).Kick(NotAuthenticatedCloseCode, "this server is not configured to host characters");
            return;
        }

        // ── Read-or-create, on the TICK's transaction and deliberately not through CharacterStore ──────────────────────────────
        //
        // CharacterStore.ReadOrCreate is the same decision for an OFF-TICK caller: it opens its own transactions and catches the
        // unique-index refusal, which is the shape A4 specifies. It cannot be used here, and the reason is the atomicity this method
        // promises. A login reads or creates the character, spawns the avatar and hands the session control in ONE commit — the tick's.
        // A nested read-or-create on its own transaction would commit separately, so a crash between the two would leave a character
        // with no avatar, and the "or none of it happens" in this method's remarks would be false.
        //
        // The retry CharacterStore performs is also not implementable here, and not needed. Not implementable: a unique-index violation
        // on a Versioned component surfaces at COMMIT, and this transaction is committed by the tick after every system has run, so a
        // catch around the spawn below would never see it. Not needed: two logins for one account cannot both reach this point. The
        // session system is serial, so there is one bind at a time, and the account guard above refuses the second session for an account
        // that is already playing. That serialisation is the precondition — stated here because it is what makes the missing retry safe,
        // and a change that makes binding parallel, or that allows one account two sessions, breaks it.
        if (!TryFindCharacter(tx, account, out var character, out var profile))
        {
            profile = new Character
            {
                Account = account,
                Name = (String64)DisplayNameFor(account),
                Credits = CharacterStore.StartingCredits,
                LastPosition = SpawnFor(account),
                LastRealm = DefaultSpawnRealm,
                LastSavedUtcTicks = DateTime.UtcNow.Ticks,
            };

            character = tx.Spawn<CharacterArch>(CharacterArch.Profile.Set(profile));
            System.Threading.Interlocked.Increment(ref _charactersCreated);
        }

        var avatar = SpawnAvatar(tx, session, in profile);

        // Control does both halves: it tells the engine which entity this session drives, and — with the AroundControlled player
        // profile — puts the session in that entity's realm and centres its sphere on it.
        subs.Session(session).Control(avatar);

        BoundPlayer[session.Value] = avatar;
        CharacterOfSession[session.Value] = character;
        SessionOfAccount[account.RawValue] = session.Value;
        AccountOfSession.Remove(session.Value);

        System.Threading.Interlocked.Increment(ref _avatarsSpawned);

        // The same counter PossessPlayers incremented, deliberately: "this session now drives a body" is the fact every check and
        // every measurement was asking about, and renaming it would make a run before A5 and a run after it incomparable.
        System.Threading.Interlocked.Increment(ref _possessions);
    }

    /// <summary>
    /// Writes live avatars' positions back to their characters on a cadence, so a hard kill costs seconds rather than a session (A6).
    /// </summary>
    /// <param name="tick">The tick context, for its number.</param>
    /// <param name="subs">Unused today; held so a future cadence can report rather than only count.</param>
    /// <param name="tx">The tick's transaction.</param>
    /// <remarks>
    /// <para>
    /// <b>Why a cadence exists at all.</b> A logout saves, and a logout is the happy path; a process killed with <c>SIGKILL</c> — or
    /// an instance an operator stops, or a crash — runs no logout, so without this a player's whole session of walking is lost. The
    /// bound on that loss is <see cref="SaveIntervalSeconds"/>, which is what makes acceptance criterion A-5 a number rather than a
    /// hope.
    /// </para>
    /// <para>
    /// <b>STAGGERED by session, which is the only interesting line here.</b> Saving every avatar on the same tick would put one
    /// component write per live player into a single tick every interval — a sawtooth in the tick time, and the busiest tick of every
    /// interval would be the one a measurement happened to notice. Offsetting by the session value spreads them over the interval,
    /// so the cost is flat and each avatar is still saved exactly once per interval.
    /// </para>
    /// <para>
    /// <b>No dirty check, deliberately.</b> A standing-still avatar is written as often as a running one. The alternative is a
    /// comparison against the saved position, which costs a read of the character to avoid a write of the character — and the write
    /// is one component on an archetype nothing walks. The saved <c>LastSavedUtcTicks</c> moving while a player stands still is also
    /// the truthful record: that position WAS current at that moment.
    /// </para>
    /// </remarks>
    private static void SaveAvatarsOnCadence(TickContext tick, SubscriptionsCommands subs, Transaction tx)
    {
        _ = subs;
        if (SaveIntervalTicks == 0 || BoundPlayer.Count == 0)
        {
            return;
        }

        ArchetypeAccessor<Player> players = default;
        ArchetypeAccessor<CharacterArch> characters = default;
        var opened = false;

        foreach (var (sessionValue, avatar) in BoundPlayer)
        {
            // The stagger: each session's save tick is its own residue of the interval, so two sessions save on the same tick only
            // when their values are congruent modulo it.
            if ((tick.TickNumber + sessionValue) % SaveIntervalTicks != 0)
            {
                continue;
            }

            if (!CharacterOfSession.TryGetValue(sessionValue, out var character))
            {
                continue;
            }

            if (!opened)
            {
                // Opened lazily and NOT disposed — the tick's transaction owns both. Lazily because on most ticks the stagger means
                // no session is due, and an accessor pair per tick for nothing is the cost this method must not have.
                players = tx.For<Player>();
                characters = tx.For<CharacterArch>();
                opened = true;
            }

            SaveAvatarLocation(players, characters, avatar, character);
        }
    }

    /// <summary>
    /// Writes every live avatar's position back to its character, once, as the server stops.
    /// </summary>
    /// <param name="tick">The tick context.</param>
    /// <remarks>
    /// <para>
    /// <b>The cadence is a bound on what a CRASH costs; it was also, wrongly, the bound on what a graceful stop cost.</b> A6's
    /// interval exists because a <c>kill -9</c> cannot be helped. An operator-initiated stop can: the avatars are still in the world
    /// and the tick still has a transaction, so there is no reason for <c>RequestShutdown</c> to lose a single step of walking.
    /// </para>
    /// <para>
    /// <b>Once.</b> A shutdown lasts several ticks while the kicks drain, and re-writing every character on each of them would turn a
    /// stop into a burst of writes proportional to the number of ticks it takes to close the sockets.
    /// </para>
    /// </remarks>
    private static void FlushAvatarsForShutdown(TickContext tick)
    {
        if (_shutdownFlushed || (BoundPlayer.Count == 0 && ResumingPlayer.Count == 0))
        {
            return;
        }

        var tx = tick.Transaction;
        if (tx == null)
        {
            return;
        }

        _shutdownFlushed = true;

        // NOT disposed: the tick's transaction owns these, as everywhere else in this file.
        var players = tx.For<Player>();
        var characters = tx.For<CharacterArch>();
        foreach (var (sessionValue, avatar) in BoundPlayer)
        {
            if (CharacterOfSession.TryGetValue(sessionValue, out var character))
            {
                SaveAvatarLocation(players, characters, avatar, character);
            }
        }

        // And the held ones (A13). A hold is deliberately out of BoundPlayer, so without this a player who dropped mid-walk and whose server was stopped
        // before their grace elapsed came back at the last cadence save — up to AvatarSaveIntervalS stale, which is the exact loss this method exists to
        // prevent. With only holds standing the early-out above used to return before writing anything at all.
        foreach (var (sessionValue, hold) in ResumingPlayer)
        {
            if (CharacterOfSession.TryGetValue(sessionValue, out var character))
            {
                SaveAvatarLocation(players, characters, hold.Avatar, character);
            }
        }
    }

    /// <summary>Whether <see cref="FlushAvatarsForShutdown"/> has run, so a multi-tick shutdown writes once.</summary>
    private static bool _shutdownFlushed;

    /// <summary>
    /// Saves and destroys the avatar of every session that is no longer open.
    /// </summary>
    /// <param name="tx">The tick's transaction.</param>
    /// <param name="players">The player accessor, owned by <paramref name="tx"/>.</param>
    /// <remarks>
    /// <b>Saved BEFORE destroyed, in that order, in one transaction.</b> The position lives on the avatar's placement, so reading it
    /// after the destroy would read a hole. Both happen in the tick's transaction, so a crash between them is not a state that
    /// exists: either the character has the new position and the avatar is gone, or neither.
    /// </remarks>
    private static void RetireAvatars(Transaction tx, ArchetypeAccessor<Player> players)
    {
        // The holds whose grace elapsed, before the subset test below: that test is about sessions, and a hold has none — its session was closed ticks or
        // minutes ago. Saved and destroyed on the same terms as any logout, because that is what this is: a logout that waited to see whether it was one.
        var expired = 0;
        for (var i = 0; i < ResumesToRetire.Count; i++)
        {
            if (ResumingPlayer.ContainsKey(ResumesToRetire[i]))
            {
                RetireHold(tx, players, ResumesToRetire[i]);
                System.Threading.Interlocked.Increment(ref _resumeHoldsExpired);
                expired++;
            }
        }

        ResumesToRetire.Clear();

        // Before the subset test below, which returns on a tick where only a hold expired. RetireHold already released each hold's own account, so this is
        // the general sweep: it catches an account whose session left without one.
        //
        // <b>Deliberately NOT suppressing the call at the end of this method.</b> A flag that said "already dropped this tick" looked like one scan saved and
        // was a defect: the retirement loop below runs AFTER this point, so skipping the later sweep left a retired session's account held for as long as the
        // process lived. The scan is over the players on the server, on a tick where a client both lost its grace and another disconnected.
        if (expired > 0)
        {
            DropAccountHolds();
        }

        // Nothing to retire means every bound session is still open: BoundPlayer is a SUBSET of Seen. Counting alone cannot say
        // that — one session leaving on the same tick as another arrives keeps both counts equal while the set membership has
        // changed completely, and an earlier `BoundPlayer.Count == Seen.Count` returned early on exactly that tick. It left the
        // departed session's avatar spatial and replicated, and its account hold standing, so a reconnect in the same tick as its
        // own disconnect was refused 4103 — the one case the class remarks promise is handled.
        //
        // The subset test is still O(1), because WalkSessions already computed the difference the other way: Unbound is
        // `Seen \ BoundPlayer`, so |Seen| = |Seen n BoundPlayer| + |Unbound|, and BoundPlayer is contained in Seen exactly when
        // |BoundPlayer| = |Seen| - |Unbound|.
        if (BoundPlayer.Count == Seen.Count - Unbound.Count)
        {
            return;
        }

        Retired.Clear();
        foreach (var (sessionValue, _) in BoundPlayer)
        {
            if (!Seen.Contains(sessionValue))
            {
                Retired.Add(sessionValue);
            }
        }

        if (Retired.Count == 0)
        {
            return;
        }

        var characters = tx.For<CharacterArch>();
        for (var i = 0; i < Retired.Count; i++)
        {
            var sessionValue = Retired[i];
            var avatar = BoundPlayer[sessionValue];

            if (CharacterOfSession.TryGetValue(sessionValue, out var character))
            {
                SaveAvatarLocation(players, characters, avatar, character);
            }

            // The avatar exists for the session and nothing else, so it goes with it. This is the demo's first in-tick Destroy on a
            // cluster archetype — the spatial index entry, the cluster slot and the EntityMap record are all released by the fence
            // that follows this tick.
            tx.Destroy(avatar);
            System.Threading.Interlocked.Increment(ref _avatarsDestroyed);

            BoundPlayer.Remove(sessionValue);
            CharacterOfSession.Remove(sessionValue);
            AccountOfSession.Remove(sessionValue);
            System.Threading.Interlocked.Increment(ref _releases);
        }

        // The account holds are dropped in a second pass: the map is keyed by account and the loop above is keyed by session, so
        // removing inside it would mean a reverse lookup per retirement.
        DropAccountHolds();
    }

    /// <summary>
    /// Saves and destroys one held avatar, whether its grace elapsed or its own account took it over.
    /// </summary>
    /// <param name="tx">The tick's transaction.</param>
    /// <param name="players">The player accessor, owned by <paramref name="tx"/>.</param>
    /// <param name="sessionValue">The old session's value, which is what the hold is keyed by.</param>
    /// <remarks>
    /// The same order and the same transaction as <see cref="RetireAvatars"/>: position read off the placement first, then the destroy, so neither reads a
    /// hole and a crash between them is not a state that exists.
    /// </remarks>
    private static void RetireHold(Transaction tx, ArchetypeAccessor<Player> players, uint sessionValue)
    {
        if (!ResumingPlayer.TryGetValue(sessionValue, out var hold))
        {
            return;
        }

        if (CharacterOfSession.TryGetValue(sessionValue, out var character))
        {
            SaveAvatarLocation(players, tx.For<CharacterArch>(), hold.Avatar, character);
        }

        tx.Destroy(hold.Avatar);
        System.Threading.Interlocked.Increment(ref _avatarsDestroyed);

        // ForgetHold drops the hold, the character, the identity and the account together, which is the same release a resume performs in the other
        // direction. Called after the save and the destroy, because the save reads the character this removes.
        ForgetHold(sessionValue);
        System.Threading.Interlocked.Increment(ref _releases);
    }

    /// <summary>Forgets the account hold of every session that no longer has an avatar.</summary>
    private static void DropAccountHolds()
    {
        AccountsToDrop.Clear();
        foreach (var (accountRaw, sessionValue) in SessionOfAccount)
        {
            // A session holding a resume still holds its account (A13): its body is standing in the world, and releasing the account would let a second
            // client log the same character in beside it. The hold is released by the resume, by the expiry, or by the account's own fresh login.
            if (!BoundPlayer.ContainsKey(sessionValue) && !ResumingPlayer.ContainsKey(sessionValue))
            {
                AccountsToDrop.Add(accountRaw);
            }
        }

        for (var i = 0; i < AccountsToDrop.Count; i++)
        {
            SessionOfAccount.Remove(AccountsToDrop[i]);
        }
    }

    /// <summary>Scratch: the account holds to drop, collected before the dictionary is written.</summary>
    private static readonly List<ulong> AccountsToDrop = [];

    /// <summary>
    /// Copies an avatar's position and realm onto its character.
    /// </summary>
    /// <param name="players">The player accessor.</param>
    /// <param name="characters">The character accessor.</param>
    /// <param name="avatar">The avatar to read.</param>
    /// <param name="character">The character to write.</param>
    /// <remarks>
    /// Position and realm together, always — a saved position without its realm is a position in the wrong world, and the demo's
    /// interiors share one coordinate box with each other. See <see cref="Character.LastRealm"/>.
    /// </remarks>
    private static void SaveAvatarLocation(ArchetypeAccessor<Player> players, ArchetypeAccessor<CharacterArch> characters,
        EntityId avatar, EntityId character)
    {
        if (!players.TryOpen(avatar, out var body) || !characters.TryOpenMut(character, out var profile))
        {
            return;
        }

        var place = body.Read(Player.Bounds);
        var realm = body.Read(Player.Realm);

        var saved = profile.Read(CharacterArch.Profile);
        saved.LastPosition = new Point3F { X = place.X, Y = place.Y, Z = place.Z };
        saved.LastRealm = realm.Value;
        saved.Credits = body.Read(Player.Inventory).Credits;
        saved.LastSavedUtcTicks = DateTime.UtcNow.Ticks;
        profile.Set(CharacterArch.Profile, saved);

        System.Threading.Interlocked.Increment(ref _locationsSaved);
    }

    /// <summary>
    /// Reads the character belonging to an account, through the account index, inside the tick's transaction.
    /// </summary>
    /// <param name="tx">The tick's transaction.</param>
    /// <param name="account">The account.</param>
    /// <param name="character">The character's entity on success.</param>
    /// <param name="profile">A copy of its component on success.</param>
    /// <returns><see langword="true"/> when the account already has a character.</returns>
    /// <remarks>
    /// <b>Asked for <c>long</c>, because that is the key type an indexed entity reference gets.</b> <c>EntityLink&lt;T&gt;</c> and a
    /// bare <c>EntityId</c> both map to <c>FieldType.Long</c> — the same eight bytes — so the tree is a <c>LongSingleBTree</c>.
    /// Asking for <c>EntityLink&lt;AccountArch&gt;</c> or <c>ulong</c> fails a cast at run time rather than at compile time, which in
    /// here would be a tick abort on somebody's second login.
    /// </remarks>
    private static bool TryFindCharacter(Transaction tx, EntityId account, out EntityId character, out Character profile)
    {
        character = default;
        profile = default;

        var key = unchecked((long)account.RawValue);
        using var found = tx.EnumerateIndex<Character, long>(CharacterByAccount, key, key);
        if (!found.MoveNext())
        {
            return false;
        }

        character = EntityId.FromRawValue((ulong)found.CurrentEntityPK);
        profile = found.CurrentComponent;
        return true;
    }

    /// <summary>
    /// Spawns a body for a session at its character's saved position and realm.
    /// </summary>
    /// <param name="tx">The tick's transaction.</param>
    /// <param name="session">The session that will drive it.</param>
    /// <param name="profile">The character this is the body of.</param>
    /// <returns>The avatar.</returns>
    /// <remarks>
    /// <para>
    /// <b>Possessed at birth, not possessed afterwards.</b> <c>Player.Control.Kind</c> and <c>Player.Session.Controller</c> are set
    /// in the spawn itself rather than written through <c>OpenMut</c> a moment later, so there is no tick — not even part of one — in
    /// which this body exists as something <c>PlayerThink</c> would drive. The pooled crowd's players are spawned without these and
    /// stay <c>Scripted</c> for ever.
    /// </para>
    /// <para>
    /// <b>Idle and stationary, with the destination on the spawn point.</b> A body that arrived already travelling would take its
    /// first step before its client had seen it, and a zero destination would send it to the planet's origin.
    /// </para>
    /// </remarks>
    private static EntityId SpawnAvatar(Transaction tx, SessionId session, in Character profile)
    {
        var bounds = default(PlayerPlacement);
        bounds.SetAt(profile.LastPosition.X, profile.LastPosition.Z, profile.LastPosition.Y, 1f);

        var state = new PlayerState
        {
            Activity = PlayerActivity.Idle,
            ActivityTicks = 0,
            MissionX = profile.LastPosition.X,
            MissionZ = profile.LastPosition.Z,
            HomeCity = -1,
            ShuttleFrom = -1,
            ShuttleDest = -1,
        };

        var move = new PlayerMotion
        {
            SpeedMps = 0f,
            DestX = profile.LastPosition.X,
            DestZ = profile.LastPosition.Z,
            // A14: the speeds this player is entitled to, from the same function the intents go through, so the client predicting its own avatar integrates
            // the figure the server will. Set at spawn rather than on each intent because the owner group travels on ENTER — a client that had to wait for
            // its first MoveDir to learn its own run speed would stutter on the first key press of every session.
            WalkMps = SpeedFor(SpeedClasses.Walk),
            RunMps = SpeedFor(SpeedClasses.Run),
            BurstMps = SpeedFor(SpeedClasses.Burst),
            SpeederMps = SpeedFor(SpeedClasses.Speeder),
            SpeederFastMps = SpeedFor(SpeedClasses.SpeederFast),
        };

        var vitals = new PlayerVitals
        {
            Health = AvatarMaxHealth,
            MaxHealth = AvatarMaxHealth,
            AttackDamage = AvatarAttackDamage,
            AttackCooldown = 0,
        };

        var inventory = new Inventory { Credits = profile.Credits, ItemCount = 0, ItemValue = 0 };

        var control = default(PlayerControl);
        control.Kind = ControllerKind.Human;

        var owner = default(PlayerSession);
        owner.Controller = session.Value;
        owner.Target = EntityId.Null;

        return tx.Spawn<Player>(
            Player.Bounds.Set(in bounds),
            Player.Move.Set(in move),
            Player.State.Set(in state),
            Player.Vitals.Set(in vitals),
            Player.Inventory.Set(in inventory),
            Player.Control.Set(in control),
            Player.Session.Set(in owner),
            Player.Realm.Set(new PlayerRealm(profile.LastRealm)));
    }

    /// <summary>An avatar's health. The pooled crowd's 1400, so a human is neither tougher nor more fragile than the world it is in.</summary>
    private const int AvatarMaxHealth = 1400;

    /// <summary>An avatar's attack damage, matching the pooled crowd's.</summary>
    private const int AvatarAttackDamage = 95;

    /// <summary>
    /// The display name a brand-new character gets.
    /// </summary>
    /// <param name="account">The account it belongs to.</param>
    /// <returns>A name.</returns>
    /// <remarks>
    /// <b>Derived from the account id rather than read from the account, and that is a stated shortcut.</b> Reading
    /// <c>Account.Name</c> would mean a second index read per first login for a string the client cannot be shown anyway — the
    /// projection compiler refuses <c>Str</c>, so a name reaches a client as an interned <c>u32</c> through A12's catalog, which is
    /// not built. Until it is, the name is a placeholder nobody renders; A8's login screen is where a person gets to choose one.
    /// </remarks>
    private static string DisplayNameFor(EntityId account) => "player_" + account.RawValue.ToString("x", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Where a brand-new character for this account arrives: a point in the spawn disc, or its centre when there is no disc.</summary>
    /// <param name="account">The account, which is also the seed.</param>
    /// <returns>The position, on the ground.</returns>
    /// <remarks>
    /// <para>
    /// <b>Derived from the account and not drawn at random, which is the part worth keeping.</b> The same account comes back to the
    /// same doorstep every time its character is re-created, a test gets a reproducible world without pinning a seed, and there is no
    /// shared RNG for a parallel bind to contend on — <see cref="BindAvatars"/> is serial today and this does not quietly depend on it
    /// staying that way.
    /// </para>
    /// <para>
    /// splitmix64's finalising mix, because the account ids are a counter and the thing being asked of them is that NEIGHBOURING ids
    /// land in unrelated places. The square root is what makes the disc uniform: without it, half the arrivals would be inside the
    /// inner 25 % of the area and the stack this exists to break up would simply be smaller.
    /// </para>
    /// </remarks>
    private static Point3F SpawnFor(EntityId account)
    {
        if (SpawnAreas.Length == 0 || SpawnGroundAt == null)
        {
            return DefaultSpawn;
        }

        // Two rounds of splitmix64's finalising mix: one picks the town, the other places the arrival inside it. One round does not
        // have the bits to spare — the town needs its own stream, and carving a third field out of 64 bits would correlate the choice
        // of town with the position inside it, which is the one pair that must stay independent.
        var h = Mix(account.RawValue);
        var pick = (h >> 32) / 4294967296d;

        var area = SpawnAreas[^1];
        foreach (var candidate in SpawnAreas)
        {
            if (pick < candidate.Weight)
            {
                area = candidate;
                break;
            }
        }

        var p = Mix(h);
        var r = Math.Sqrt((p & 0xFFFFFFFFUL) / 4294967296d) * area.RadiusM;
        var a = (p >> 32) / 4294967296d * Math.PI * 2d;
        var x = (float)(area.X + (r * Math.Cos(a)));
        var z = (float)(area.Z + (r * Math.Sin(a)));

        return new Point3F { X = x, Y = SpawnGroundAt(x, z), Z = z };
    }

    /// <summary>splitmix64's finalising mix: decorrelates the near-sequential account ids this is seeded from.</summary>
    /// <param name="value">The seed.</param>
    /// <returns>The mixed value.</returns>
    private static ulong Mix(ulong value)
    {
        value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }

    /// <summary>
    /// How many sessions the avatar bookkeeping is tracking, by map: for the checks that a session leaves nothing behind.
    /// </summary>
    /// <remarks>
    /// <b>Exposed because a leak here is invisible to every other observable.</b> The population census, the spawn and destroy
    /// counters and the client's own view all come back to where they started while these dictionaries keep growing — the suite had a
    /// twenty-round leak case that stayed green across exactly that. Read from the tick thread's own writes without a lock, which is
    /// safe for the checks' purpose: every case reads it between ticks, after the thing it was waiting for.
    /// </remarks>
    public static (int Identities, int Bound, int Characters, int AccountHolds) SessionAccounting
        => (AccountOfSession.Count, BoundPlayer.Count, CharacterOfSession.Count, SessionOfAccount.Count);

    /// <summary>Forgets every avatar binding. Called from <see cref="ResetSessionAccounting"/>.</summary>
    private static void ResetAvatarAccounting()
    {
        AccountOfSession.Clear();
        CharacterOfSession.Clear();
        SessionOfAccount.Clear();
        AccountsToDrop.Clear();
        ResumingPlayer.Clear();
        ResumesToRetire.Clear();
        System.Threading.Interlocked.Exchange(ref _avatarsSpawned, 0);
        System.Threading.Interlocked.Exchange(ref _avatarsDestroyed, 0);
        System.Threading.Interlocked.Exchange(ref _charactersCreated, 0);
        System.Threading.Interlocked.Exchange(ref _loginsRefusedDuplicate, 0);
        System.Threading.Interlocked.Exchange(ref _loginsRefusedAnonymous, 0);
        System.Threading.Interlocked.Exchange(ref _loginsRefusedUnconfigured, 0);
        System.Threading.Interlocked.Exchange(ref _locationsSaved, 0);
        System.Threading.Interlocked.Exchange(ref _resumesHeld, 0);
        System.Threading.Interlocked.Exchange(ref _resumesRebound, 0);
        System.Threading.Interlocked.Exchange(ref _resumeHoldsExpired, 0);
        System.Threading.Interlocked.Exchange(ref _resumeExpiriesWithoutHold, 0);
        System.Threading.Interlocked.Exchange(ref _resumeHoldsTakenOver, 0);
        _shutdownFlushed = false;
        AvatarsConfigured = false;
        SaveIntervalTicks = 0;
        SaveIntervalSeconds = 0d;
    }
}
