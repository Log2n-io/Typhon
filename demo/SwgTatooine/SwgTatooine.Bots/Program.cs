using SwgTatooine.Server;
using SwgTatooine.Bots;
using System;
using System.Threading;
using System.Threading.Tasks;

// SwgTatooine.Bots — point a population of scripted god cameras at a running server and report what happened to them.
//
//   SwgTatooine.Bots --endpoint ws://127.0.0.1:9100/ws --bots 100 --seconds 3600
//
// The report is the product. A load generator that only prints "done" cannot distinguish a server that carried a hundred
// sessions for an hour from one that shed them in the first minute and left the generator reconnecting.
//
// The default is a WEBSOCKET endpoint because that is what `SwgTatooine.Server --serve <port>` listens on, and nothing else. A tcp://
// default read plausibly and connected to nothing: the swarm printed its first line and exited zero, having measured a server it
// never reached. The scheme and the port have to name what the server actually serves.

// The flags are the knobs an A/B moves, so they are parsed as strictly as the server's (#1072): an unknown, misspelt, repeated or malformed flag exits 2
// naming the token, instead of running the default — a mistyped --bots used to give two identical arms under a report that said otherwise.
string endpoint, kind, fuzzMode, accountsFile, authBaseArg;
Uri endpointUri;
int bots, seconds, hz, connectBatch, connectRate, tourSeconds, planets, fuzz, fuzzRate, signupConcurrency, accountBase, maxBots, chunkSize;
double regionM, wanderSeconds;
bool brain;
bool noMissions;
string homeCity;
double homeShare;
int homeMax;
double tipSeconds;
try
{
    var defaults = new BotSwarmOptions();
    var r = new ArgReader(args, Usage);
    r.Group("swarm");
    r.Switch("--help", "print this list and exit (or -h as the first argument)");
    endpoint = r.Str("--endpoint", "ws://127.0.0.1:9100/ws", "the server's WebSocket endpoint (`SwgTatooine.Server --serve <port>` listens at ws://host:<port>/ws)",
        "<uri>");
    bots = r.Int("--bots", defaults.Count, "sessions to open", min: 1);

    // The ceiling the population may later be moved to, which is also how many accounts are provisioned up front. Headless has
    // no dial, but it shares the swarm with the control panel and a run started here can be inspected by one -- and the accounts
    // file is the thing both of them reuse.
    maxBots = r.Int("--max-bots", 0, "ceiling the population may be turned up to; 0 is --bots", min: 0);

    // The pipeline's unit of work: this many bots are signed up, connected and handed to the driver as one step, so the chunk before
    // is already walking while this one signs up. Deliberately not `rate x stagger`, which is 8 at 400/s -- a fine quantum for a
    // socket and a poor one for a batch of signups.
    chunkSize = r.Int("--chunk-size", defaults.ChunkSize, "bots provisioned, connected and driven as one step", min: 1);
    seconds = r.Int("--seconds", 60, "the measured window, from the moment every session is open", min: 1);
    hz = r.Int("--hz", defaults.TickHz, "the driver's tick rate", min: 1);
    kind = r.Str("--kind", defaults.Kind, "the session kind the server's admission expects", "<kind>");
    connectBatch = r.Int("--connect-batch", defaults.ConnectBatch, "sessions opened per connect step", min: 1);

    // The rail the ramp opens sessions at, where --connect-batch says how many fall between two 20 ms staggers; an explicit batch
    // still wins, which is why the batch default is the 0 sentinel BotSwarmOptions documents. Low on purpose -- the cost of
    // admitting faster is on the SERVER (each avatar is spawned serially inside the tick's transaction, and a just-admitted
    // session is what #1024 stalls the replication track on), not on the socket.
    connectRate = r.Int("--connect-rate", 0, "sessions per second the ramp opens; 0 leaves --connect-batch in charge", min: 0);

    // A god camera's region edge: what a ClientRegion god profile (the server's --god-region) observes.
    regionM = r.Dbl("--region-m", defaults.RegionRadiusM, "a god camera's region edge, metres", min: 1);

    // --kind player only: MoveDir is Roles(Player). 0 leaves the avatars standing where they spawned, which is what every run before
    // this one measured -- replication only, with the intent path, the spatial index and cluster migration untouched.
    wanderSeconds = r.Dbl("--wander-seconds", defaults.WanderSeconds, "seconds a player bot holds a heading; 0 stands still", min: 0d);

    // --kind player only, and it REPLACES wandering: a thinking bot travels between cities, idles in them, hunts what it finds and takes
    // shuttles, which clumps the population and changes the shape of every interest set. Off by default because that is a different
    // workload rather than a better version of the same one, and flipping it silently would make two runs incomparable.
    brain = r.Switch("--brain", "player bots decide for themselves from what they are sent, instead of wandering");
    // --brain only: a bot takes missions at the terminals it visits unless told not to — a measurement run's workload stays what it was.
    noMissions = r.Switch("--no-missions", "thinking bots take no missions at the terminals they visit");
    // --brain only: a share of the population lives in one city and never leaves it, capped so a huge run does not put thousands there.
    homeCity = r.Str("--home-city", defaults.HomeCity, "the city a share of the thinking bots live in; 'none' for none", "<name>");
    homeShare = r.Dbl("--home-share", defaults.HomeShare, "the share of the bots that live in --home-city", min: 0d);
    homeMax = r.Int("--home-max", defaults.HomeMax, "the most bots that live in --home-city", min: 0);
    // --brain only: a tip is a ledger row, so tipping is its own workload and off unless asked for (economy-foundation.md E0-11).
    tipSeconds = r.Dbl("--tip-seconds", 0d, "a thinking bot tips a named player near it about this often, seconds; 0 never tips", min: 0d);
    // Every god camera moves to the next planet on this period (Realms G3); the report counts the switches applied and the RealmNews heard.
    tourSeconds = r.Int("--god-tour", defaults.TourEverySeconds, "seconds between planet switches; 0 never tours", min: 0);
    planets = r.Int("--planets", defaults.Planets, "planets a touring camera cycles through", min: 1);
    // Registered rather than read ad hoc, because RejectUnknown is what makes a misspelt flag exit 2: an --accounts-file the
    // reader does not know about is an unknown token, so a population that was meant to have real accounts would be refused
    // outright instead of signed up.
    r.Group("accounts — a population of real players instead of anonymous cameras (B5)");
    accountsFile = r.Str("--accounts-file", null, "where names, tokens and resume tokens are kept; required by --kind player", "<path>");
    authBaseArg = r.Str("--auth-base", null, "the /auth endpoints to sign up against; derived from --endpoint when absent", "<url>");
    signupConcurrency = r.Int("--signup-concurrency", 8, "concurrent signups while provisioning", min: 1);

    // One population across several processes: give each its own slice of the account name space. Ten thousand sessions in one process
    // does not work -- it dies of its own inbound traffic while the server is still inside its tick budget -- so a run that big is
    // five processes of two thousand, each with its own --account-base and its own accounts file.
    accountBase = r.Int("--account-base", 0, "added to every bot index before deriving its account name", min: 0);

    r.Group("fuzz — hostile connections instead of a swarm (AC-17's live half)");
    fuzz = r.Int("--fuzz", 0, "hostile clients to open; 0 runs the swarm", min: 0);
    fuzzRate = r.Int("--fuzz-rate", 500, "messages per second per fuzz client; 0 is the control arm", min: 0);
    fuzzMode = r.Choice("--fuzz-mode", "mixed", ["mixed", "valid", "churn", "mixed-slow"], "what the fuzz clients send");
    if (r.WantsHelp)
    {
        Console.Write(r.Help());
        return 0;
    }
    r.RejectUnknown();
    if (!Uri.TryCreate(endpoint, UriKind.Absolute, out endpointUri))
    {
        throw new ArgumentException($"'--endpoint' takes an absolute URI such as ws://127.0.0.1:9100/ws, not '{endpoint}'.");
    }
}
catch (ArgumentException e)
{
    Console.Error.WriteLine(e.Message);
    return 2;
}

if (fuzz > 0)
{
    // Refused rather than ignored. The fuzzer is not a swarm: it opens hostile connections and never signs anything up, so a
    // command line combining it with the account flags is asking for two different runs. Taking the fuzz branch silently would
    // produce a run that did none of what the other half of the line asked for — the same class of quiet nothing that
    // --god-tour was doing with its misspelt field name.
    if (accountsFile != null || string.Equals(kind, "player", StringComparison.Ordinal))
    {
        Console.Error.WriteLine("--fuzz runs hostile connections instead of a swarm; it cannot be combined with --kind player or --accounts-file");
        return 2;
    }

    using var fuzzStop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
    await new Fuzzer(endpointUri, fuzz, fuzzRate, fuzzMode).RunAsync(fuzzStop.Token);
    return 0;
}

// --accounts-file <path> [--auth-base <url>] [--signup-concurrency <n>]: give the population real accounts (B5).
//
// A god session is anonymous and read-only, so --kind god needs none of this. A PLAYER session IS its account — the server's
// admission hook validates the token and carries the account through as the thing that decides which avatar to bind — so a
// thousand player bots need a thousand accounts, signed up through the same /auth endpoints a browser posts to.
//
// SWG_BOT_KEY must be in the environment. Passwords are DERIVED from it and the bot's index and are never written anywhere: the
// accounts file holds names, tokens and resume tokens, so losing it costs a round of logins and losing the key costs nothing.
var isPlayer = string.Equals(kind, "player", StringComparison.Ordinal);
if (isPlayer && accountsFile == null)
{
    Console.Error.WriteLine(
        "--kind player needs --accounts-file <path>: the server refuses a player session without a token (close 4102), so the "
        + "population has to be signed up first. --kind god needs no accounts.");
    return 2;
}

BotAccountStore accounts = null;

// Kept for the run's whole length, because the ramp signs each chunk up as it reaches it. Disposed after the swarm, so no chunk can
// be mid-signup when its HttpClient goes away.
BotProvisioner fleetProvisioner = null;
if (accountsFile != null && !isPlayer)
{
    // Said, not silently ignored. A god session carries no token at all, so provisioning one would sign up a thousand accounts
    // nothing presents and would demand a key the run does not need -- and run-fleet.sh passes --accounts-file unconditionally
    // because it does not know which kind it is being asked for.
    Console.WriteLine($"--kind {kind} sessions are anonymous: {accountsFile} is not read and no accounts are provisioned");
}
else if (accountsFile != null)
{
    var authBase = authBaseArg != null ? new Uri(authBaseArg) : BotProvisioner.AuthBaseFrom(endpointUri);
    if (authBase == null)
    {
        Console.Error.WriteLine($"--endpoint {endpoint} has no HTTP side to sign up against; pass --auth-base http://host:port");
        return 2;
    }

    string fleetKey;
    try
    {
        fleetKey = BotCredentials.ReadKeyOrThrow();
    }
    catch (InvalidOperationException e)
    {
        // The whole message, not a summary: it is the only thing standing between the operator and a thousand orphaned accounts.
        Console.Error.WriteLine(e.Message);
        return 2;
    }

    // Provisioned to the CEILING, not the starting count: the accounts have to exist before the population can be turned up to
    // them, and signing up thousands while an operator waits on a dial is not a thing to do at that moment.
    var accountCapacity = maxBots > 0 ? Math.Max(bots, maxBots) : bots;
    accounts = new BotAccountStore(accountsFile, accountCapacity);
    Console.WriteLine($"accounts: {accounts.FilePath} ({accounts.UsableTokens(600)} of {accountCapacity} tokens still usable)");
    if (accounts.SkippedLines > 0)
    {
        // Said out loud rather than absorbed: a skipped line becomes a login, and a run that silently re-signed-up part of its
        // fleet would look like a churn measurement when it was a parse failure.
        Console.WriteLine($"  !! {accounts.SkippedLines} line(s) in the accounts file could not be parsed and were skipped");
    }

    // <b>Not provisioned here any more: the swarm asks for each chunk as the ramp reaches it.</b> Signing the whole population up
    // first meant nothing existed for seventeen seconds at ten thousand, and it is why the ceiling used to cost its full price
    // immediately. The provisioner is kept alive for the run's whole length, because the ramp calls back into it.
    fleetProvisioner = new BotProvisioner(authBase, accounts, fleetKey, signupConcurrency, accountBase);
    Console.WriteLine($"accounts are signed up a chunk at a time as the ramp reaches them, against {authBase}");
}

var options = new BotSwarmOptions
{
    Endpoint = endpointUri,
    Count = bots,
    MaxCount = maxBots,
    ChunkSize = chunkSize,

    // Null for a god fleet: anonymous sessions need no account, so every chunk opens straight away.
    EnsureAccounts = fleetProvisioner == null
        ? null
        : async (from, count, ct) =>
        {
            var report = await fleetProvisioner.EnsureRangeAsync(from, count, null, ct);
            var ready = report.Created + report.LoggedIn + report.Reused;
            if (report.Failed > 0)
            {
                Console.WriteLine($"  !! chunk {from}..{from + count - 1}: {report.Failed} account(s) failed, first: {report.FirstFailure}");
            }

            return ready;
        },
    TickHz = hz,
    ConnectBatch = connectBatch,
    ConnectRatePerSecond = connectRate,
    Kind = kind,
    RegionRadiusM = regionM,
    WanderSeconds = wanderSeconds,
    Brain = brain,
    TipSeconds = tipSeconds,
    Missions = !noMissions,
    HomeCity = homeCity,
    HomeShare = homeShare,
    HomeMax = homeMax,
    TourEverySeconds = tourSeconds,
    Planets = planets,

    // Null when there is no accounts file, which is what keeps --kind god anonymous: the client writes an empty token and the
    // server's hook takes the god branch, where there is no identity to authorise anything.
    TokenFor = accounts == null ? null : index => accounts[index]?.Token,
    ResumeFor = accounts == null ? null : accounts.Resume,
    OnResumeToken = accounts == null
        ? null
        : (index, token) =>
        {
            accounts.PutResume(index, token);

            // Batched: a thousand sessions each persisting 16 bytes is one write per 64 of them, not a thousand writes on a path
            // the measurement does not care about. The final flush below catches the remainder.
            accounts.FlushIfDue();
        },
};

Console.WriteLine($"connecting {bots} {kind} bots to {endpoint} …");

// Declared BEFORE the swarm so it is disposed AFTER it — `using` unwinds in reverse — and no chunk can be mid-signup when the
// provisioner's HttpClient goes away.
using var provisionerLifetime = fleetProvisioner;
await using var swarm = new BotSwarm(options);
// Armed only once every session is open: armed here, a connect phase longer than `--seconds` left no measured window at all, and every figure
// below was read the moment the last session joined.
using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stopping.Cancel();
};

var opened = await swarm.StartAsync(CancellationToken.None);
Console.WriteLine($"opened {opened} of {bots} sessions");
stopping.CancelAfter(TimeSpan.FromSeconds(seconds));

// A swarm that opened nothing has measured nothing, and every figure below it would be a well-formatted zero. It is reported as a
// failure here rather than left to the reader, because the numbers that follow are the kind a reader trusts.
if (opened == 0)
{
    Console.Error.WriteLine($"no session opened against {endpoint}. Check the scheme and the port: `SwgTatooine.Server --serve <port>` "
        + "listens for WEBSOCKET connections at ws://host:<port>/ws and for nothing else.");
    return 2;
}

if (opened < bots)
{
    // <b>With the reason, which this line used to leave out.</b> A fleet asking for 10 000 against a server whose session table holds
    // 8 192 printed "only 8192 of 10000 opened" and nothing else — so a hard, named, server-side refusal was indistinguishable from a
    // generator that had run out of sockets, threads or memory, each of which was investigated before the real limit was found.
    var (refused, reason) = swarm.OpenFailures;
    Console.Error.WriteLine($"only {opened} of {bots} sessions opened, so everything below is measured on a smaller population "
        + "than was asked for.");
    if (refused > 0)
    {
        Console.Error.WriteLine($"  {refused} session(s) were refused or failed to open. First reason: {reason}");
    }
}

var started = DateTime.UtcNow;

// Bytes counted from the same instant as the window, not from the first connect: dividing everything received since the first session opened by the
// time since the last one did overstated the rate by the connect phase's share.
var bytesAtStart = swarm.BytesReceived;

// Every 5 s sample, for the rate over the run's last 10 s: a per-session budget is judged on its steady state, not on the fill that opens a run.
var samples = new System.Collections.Generic.List<(DateTime At, long Bytes)> { (started, bytesAtStart) };
while (!stopping.IsCancellationRequested)
{
    try
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stopping.Token);
    }
    catch (OperationCanceledException)
    {
        break;
    }

    samples.Add((DateTime.UtcNow, swarm.BytesReceived));

    // The server's own numbers, as the server reported them to its clients (AC-1's track p99 is the headline).
    Console.WriteLine($"  t+{(DateTime.UtcNow - started).TotalSeconds,6:F0}s  connected {swarm.Connected,5}  "
        + $"messages {swarm.MessagesReceived,10}  faults {swarm.Faults}  "
        + $"frames {Frames()}  "
        + $"stats {Blocks()}  "
        + $"sessions {swarm.ServerMetric("typhon.sessions"),5:F0}  "
        + $"track p99 {swarm.ServerMetric("typhon.subscriptions.track.p99"),7:F3} ms  "
        + $"tick p99 {swarm.ServerMetric("typhon.tick.p99"),7:F3} ms"
        + Home()
        + Errands());
}

// The bots that live in the home city, and how many stand in it now: the concentration, watched as it builds.
string Home()
{
    var (living, there) = swarm.Homes();
    return living == 0 ? string.Empty : $"  home {there}/{living} in {homeCity}";
}

// Walks searched, and walks out of a shuttleport's plaza: a fleet trapped in a port shows as the second climbing while the first stands still.
string Errands()
{
    var e = swarm.Errands();
    return e.Paths == 0 && e.Ports == 0 ? string.Empty : $"  walks {e.Paths} ports left {e.Ports} in a port {e.InPort}";
}

// TICK frames applied across the population: a connected session that is never produced for looks healthy in every other number.
string Frames()
{
    var (total, min, max, unserved) = swarm.Frames();
    return $"{total,7} total {min}..{max}/session, {unserved} unserved, {swarm.RecordsPerFrame:F0} records/frame";
}

// STATS blocks received across the population: the count, not a value, because a value cannot tell "stopped sending" from "stopped moving".
string Blocks()
{
    var (total, min, max, starved) = swarm.StatsBlocks();
    return $"{total,6} total {min}..{max}/session, {starved} with none, {swarm.StatsGranted} granted";
}

// The measurement line: what fraction of a tick replication costs, at this session count and view shape. One line so a sweep is greppable.
{
    var systems = swarm.LabelledMetric("typhon.system.mean", 64);
    double Mean(string name)
    {
        foreach (var (n, ms) in systems)
        {
            if (n == name)
            {
                return ms;
            }
        }

        return 0;
    }

    var project = Mean("SubscriptionsProject");
    var frames = Mean("SubscriptionsFrames");
    var subs = project + frames;
    var tick = swarm.ServerMetric("typhon.tick.p50");
    var tick99 = swarm.ServerMetric("typhon.tick.p99");
    // The track's own span p99 (P1-17), which the Phase 2 criteria are written against; `subs` above is a sum of two stages' means.
    var track99 = swarm.ServerMetric("typhon.subscriptions.track.p99");
    Console.WriteLine();
    // Wire cost beside CPU cost: bytes per session per second is the number a capacity plan is actually built on, and a design that trades CPU for
    // payload (or the reverse) cannot be judged from the timing half alone.
    var elapsed = Math.Max(1.0, (DateTime.UtcNow - started).TotalSeconds);
    var bytesPerSessionPerSec = bots > 0 ? (swarm.BytesReceived - bytesAtStart) / elapsed / bots : 0;
    var now = DateTime.UtcNow;
    var from = samples[0];
    foreach (var sample in samples)
    {
        if ((now - sample.At).TotalSeconds >= 10)
        {
            from = sample;
        }
    }

    var tailSeconds = Math.Max(1.0, (now - from.At).TotalSeconds);
    var bytesLast10 = bots > 0 ? (swarm.BytesReceived - from.Bytes) / tailSeconds / bots : 0;
    Console.WriteLine($"SWEEP kind={kind} sessions={bots} project={project:F3} frames={frames:F3} "
        + $"subs={subs:F3} trackP99={track99:F3} tickP50={tick:F3} tickP99={tick99:F3} subsPct={(tick > 0 ? subs / tick * 100 : 0):F1} recPerFrame={swarm.RecordsPerFrame:F0} "
        + $"bytesPerSessionPerSec={bytesPerSessionPerSec:F0} bytesLast10={bytesLast10:F0} totalBytes={swarm.BytesReceived}");
    // Every system's mean, heaviest first: the tick is more than replication, and a change that moves cost out of the three stages above shows up here.
    var bySystem = new System.Collections.Generic.List<(string Name, double Ms)>(systems);
    bySystem.Sort((x, y) => y.Ms.CompareTo(x.Ms));
    Console.WriteLine("SYSTEMS " + string.Join(" ", bySystem.ConvertAll(x => $"{x.Name}={x.Ms:F3}")));
}

Console.WriteLine();
Console.WriteLine("server systems by mean duration:");
foreach (var (name, ms) in swarm.LabelledMetric("typhon.system.mean", 4))
{
    Console.WriteLine($"  {ms,8:F3} ms  {name}");
}

Console.WriteLine("live entities per archetype:");
foreach (var (name, count) in swarm.LabelledMetric("typhon.archetype.entities", 8))
{
    Console.WriteLine($"  {count,9:N0}  {name}");
}

Console.WriteLine();
Console.WriteLine($"ran {(DateTime.UtcNow - started).TotalSeconds:F0}s over {swarm.Ticks} driver ticks");

// <b>Against the server's silence window, because that comparison is the whole diagnosis.</b> A session the fleet has not pinged
// within 3 / pingHz seconds is closed with 4001 — 750 ms at the 4 Hz the server advertises — so a ping sweep approaching that is
// the reason a population settles below its target, and a sweep well inside it rules the fleet's own cadence out.
var pingCost = swarm.PingCost;
var proc = System.Diagnostics.Process.GetCurrentProcess();

// <b>The collector, because a sweep whose MEAN is 66 ms and whose WORST is 844 ms is not slow, it is stopped.</b> A pause longer
// than the server's silence window closes every session the fleet was about to ping, so the pause total is the figure that says
// whether a lost population was a cost problem or a garbage problem — and this fleet's first answer was 700 MB/s of it.
Console.WriteLine(
    $"gc: {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)} gen0/1/2, "
    + $"{GC.GetTotalPauseDuration().TotalMilliseconds:F0} ms paused in total, {GC.GetTotalAllocatedBytes() / 1048576.0:F0} MiB allocated, "
    + $"heap {GC.GetTotalMemory(false) / 1048576.0:F0} MiB");
Console.WriteLine(
    $"ping cost: {pingCost.MeanUs:F1} us mean, {pingCost.WorstUs:F0} us worst over {pingCost.Count} sends; "
    + $"pool {ThreadPool.ThreadCount} threads, {ThreadPool.PendingWorkItemCount} queued; "
    + $"process CPU {proc.TotalProcessorTime.TotalSeconds:F1} s over {(DateTime.UtcNow - started).TotalSeconds:F0} s wall");

var pingSweep = swarm.PingSweepMs;
var driveSweep = swarm.DriveSweepMs;
Console.WriteLine(
    $"sweeps: ping {pingSweep.Last:F0} ms (worst {pingSweep.Worst:F0}), drive {driveSweep.Last:F0} ms (worst {driveSweep.Worst:F0}) "
    + "— the server closes a session it has not heard from for 3/pingHz seconds");
Console.WriteLine($"connected at end: {swarm.Connected} of {opened}");
Console.WriteLine($"messages received: {swarm.MessagesReceived}");
Console.WriteLine($"receive-loop faults: {swarm.Faults}");

if (accounts != null)
{
    // Unconditional, because FlushIfDue leaves up to a threshold's worth on the floor, and those are exactly the resume tokens a
    // restarted fleet would need. One write at the end costs nothing and is the difference between a warm restart and a cold one.
    accounts.Flush();
    Console.WriteLine($"accounts file written {accounts.Flushes} time(s): {accounts.FilePath}");
}

// Ours, not the network's: a field or command this program asked for and the catalog does not describe. Printed before the per-feature counters below,
// because a fault here means those counters are describing a run that did not send what it thought it was sending — which is how --god-tour spent its
// whole life asking for nothing while every number said the run was healthy.
var (faultKinds, faultTotal) = swarm.ProtocolFaults();
if (faultKinds > 0)
{
    Console.WriteLine($"PROTOCOL FAULTS: {faultTotal} across {faultKinds} distinct message(s) — the counters below are NOT a clean measurement");
    swarm.ReportProtocolFaults();
}
var (toursAsked, realmSwitches, news, inRealm) = swarm.Realms();
Console.WriteLine($"realms: {toursAsked} tours asked, {realmSwitches} realm switches applied, {news} RealmNews heard, {inRealm} of {opened} sessions holding a realm");

// Beside the sessions, because a fleet of bound avatars that sends none of these is a replication test wearing a player test's name.
// Which half of the pipeline bound the ramp. Printed unconditionally, because "--connect-rate 400" is a claim about the outcome and
// this is the only line that can falsify it.
// <b>The local cause behind each abrupt close.</b> A table of "1006 x 3306" is not a diagnosis: no close frame arrived, so the
// peer's intent is unknowable, but the socket failure that revealed it is known and distinguishes a reset by the peer from a local
// abort from a timeout. Without this the count is a dead end, which is precisely where a 5 000-session run stalled.
var closeDetails = swarm.CloseDetails();
if (closeDetails.Count > 0)
{
    Console.WriteLine("abnormal closes by local cause:");
    foreach (var (detail, count) in closeDetails)
    {
        Console.WriteLine($"  {detail}: {count}");
    }
}

var cost = swarm.ChunkCost();
if (cost.Chunks > 0)
{
    Console.WriteLine(
        $"ramp: {cost.Chunks} chunk(s), mean {cost.MeanProvisionMs:F0} ms provisioning + {cost.MeanConnectMs:F0} ms connecting; "
        + $"{cost.BoundByProvisioning} bound by SIGNUPS");
}

var intentsSent = swarm.IntentsSent();
Console.WriteLine(intentsSent == 0
    ? $"movement: NO intents sent — the population stood where it spawned (--wander-seconds {wanderSeconds})"
    : $"movement: {intentsSent} MoveDir intents sent, {(double)intentsSent / Math.Max(1, opened):F0} per session");

if (brain)
{
    var decisions = swarm.Decisions();
    Console.WriteLine(
        $"brain: {decisions.Boardings} shuttle boarding(s) asked for, {decisions.Hunts} hunt(s) begun; "
        + $"{decisions.PortsKnownMax} port(s) known by the best-travelled bot, {decisions.BotsThatCanFly} bot(s) know two or more, "
        + $"{(decisions.Creatures > 0 ? "a creature was in view at least once" : "no creature was EVER in view")}");
    var homes = swarm.Homes();
    Console.WriteLine(homes.Living == 0
        ? "home: none (--home-city)"
        : $"home: {homes.Living} bot(s) live in {homeCity} (--home-share {homeShare:G}, --home-max {homeMax}); {homes.There} stood in it at their last decision");
    var missions = swarm.Missions();
    Console.WriteLine(
        $"missions: {missions.Asked} take(s) sent, {missions.Taken} taken, {missions.Completed} completed (paid), {missions.Cancelled} cancelled after "
        + $"{SwgTatooine.Bots.Brain.BotBrain.QuestSeconds:F0} s");
    var items = swarm.Items();
    Console.WriteLine($"items: {items.LootRows} loot row(s) read, {items.UnitsLost} unit(s) lost to a full inventory, {items.Stacks} stack(s) held");
    var tips = swarm.Tips();
    Console.WriteLine(tipSeconds > 0
        ? $"tips: {tips.Sent} GiveCredits sent, {tips.NobodyNear} due with nobody named within reach (--tip-seconds {tipSeconds})"
        : "tips: off (--tip-seconds 0)");
    var errands = swarm.Errands();
    Console.WriteLine(
        $"errands: {errands.Mounts} swoop mount/dismount ask(s), {errands.Doors} door crossing(s), {errands.Used} terminal(s) used, "
        + $"{errands.Paths} walk(s) searched round buildings, {errands.Ports} walk(s) out of a shuttleport's plaza");
}

if (swarm.Disconnects.Count == 0)
{
    Console.WriteLine("disconnects: none");
}
else
{
    Console.WriteLine("disconnects by close code:");
    foreach (var (code, count) in swarm.Disconnects)
    {
        Console.WriteLine($"  {Describe(code)}: {count}");
    }
}

// The verdict reads DELIVERY, not just disconnects. A run where every session connected, stayed connected and received no TICK frame at all used to exit
// zero — which is the failure this file's own remarks name ("a connected session that is never produced for looks healthy in every other number") and then
// did not check for. A driver that never fired, or a population that faulted its way through the run, exited zero too.
var (framesTotal, _, _, unserved) = swarm.Frames();
var healthy = swarm.Disconnects.Count == 0 && swarm.Faults == 0 && swarm.Ticks > 0 && framesTotal > 0 && unserved == 0;

if (!healthy)
{
    Console.Error.WriteLine(
        $"run NOT healthy: disconnects={swarm.Disconnects.Count} faults={swarm.Faults} driverTicks={swarm.Ticks} frames={framesTotal} unserved={unserved}");
}

return healthy ? 0 : 1;

static string Describe(ushort code) => code switch
{
    0 => "0 (the handshake never completed)",
    1001 => "1001 going away",
    1013 => "1013 try again later — the server shed this client under load",
    4001 => "4001 no acknowledgement — the swarm stopped pinging",
    _ => code.ToString(),
};

partial class Program
{
    /// <summary>What <c>--help</c> prints before the flags.</summary>
    private const string Usage =
        """
        SwgTatooine.Bots — scripted sessions against a running SwgTatooine.Server, and a report of what happened to them.

          usage: SwgTatooine.Bots [flags]                 a swarm of sessions for --seconds, then the report
                 SwgTatooine.Bots --fuzz <clients> [...]  hostile connections instead of a swarm

        """;
}
