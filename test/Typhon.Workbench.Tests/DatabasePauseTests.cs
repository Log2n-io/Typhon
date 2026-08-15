using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Typhon.Workbench.DataBrowser;
using Typhon.Workbench.Fixtures;
using Typhon.Workbench.Hosting;
using Typhon.Workbench.Schema;
using Typhon.Workbench.Services;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Tests;

/// <summary>
/// The paused-session lifecycle (#621): releasing a database to another process, staying useful while released, and coming back.
///
/// <para><b>Why an in-process holder is a faithful stand-in for "your application is running".</b> <c>PagedMMF.AcquireLockFile</c> decides by reading
/// <c>db.lock</c> and asking whether its recorded PID is alive — it never asks whether that PID is <i>someone else</i>. A second
/// <see cref="EngineLifecycle"/> on the same bundle inside this process therefore takes exactly the path a separate application takes, and throws the same
/// <see cref="DatabaseLockedException"/> carrying the same holder identity. No child process needed, and no timing flake.</para>
/// </summary>
[TestFixture]
[NonParallelizable] // opens real engines — the schema-compat check reads the process-global ArchetypeRegistry (see #554)
public sealed class DatabasePauseTests
{
    private string _tempDir;
    private DataBrowserService _dataBrowser;
    private SessionManager _sessions;
    private DatabasePauseCoordinator _coordinator;
    private OptionsStore _options;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "typhon-wb-pause-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _sessions = new SessionManager(NullLogger<SessionManager>.Instance);
        _dataBrowser = new DataBrowserService(_sessions);
        _options = new OptionsStore(NullLogger<OptionsStore>.Instance, Path.Combine(_tempDir, "options"));
        _coordinator = new DatabasePauseCoordinator(_sessions, _dataBrowser, _options, NullLogger<DatabasePauseCoordinator>.Instance);
        _sessions.SessionRemoved += _coordinator.Forget;
    }

    [TearDown]
    public void TearDown()
    {
        try { _coordinator?.Dispose(); } catch { /* best-effort */ }
        try { _sessions?.DisposeAll(); } catch { /* best-effort */ }
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* the OS may still hold an unmapped view briefly */ }
    }

    private async Task<OpenSession> OpenSessionAsync(string path)
    {
        var engine = await EngineLifecycle.OpenAsync(path);
        var session = new OpenSession(Guid.NewGuid(), path, engine, SessionState.Ready, engine.SchemaStatus, engine.ResolvedSchemaPaths,
            engine.LoadedComponentTypes, engine.Diagnostics);
        _sessions.Create(session);
        return session;
    }

    // ── AC2 · a locked database opens PAUSED rather than failing ─────────────────────────────────────────────────

    [Test]
    public async Task OpenAsync_WhenDatabaseIsHeld_ThrowsDatabaseLockedCarryingTheHolder()
    {
        var path = Path.Combine(_tempDir, "held.typhon");
        using var holder = await EngineLifecycle.OpenAsync(path);

        // The identity must survive the open path intact — EngineLifecycle used to flatten this into engine_open_failed, which discarded the only record of
        // WHO was holding the database and left the banner with nothing to name.
        var ex = Assert.ThrowsAsync<DatabaseLockedException>(async () => await EngineLifecycle.OpenAsync(path));
        Assert.Multiple(() =>
        {
            Assert.That(ex.OwnerPid, Is.EqualTo(Environment.ProcessId), "the in-process holder is the owner the lock file records");
            Assert.That(ex.OwnerMachine, Is.EqualTo(Environment.MachineName));
        });
    }

    [Test]
    public async Task PausedSession_BornFromALockedDatabase_HasNoEngineAndNamesTheHolder()
    {
        var path = Path.Combine(_tempDir, "held.typhon");
        using var holder = await EngineLifecycle.OpenAsync(path);

        var locked = Assert.ThrowsAsync<DatabaseLockedException>(async () => await EngineLifecycle.OpenAsync(path));
        var session = new OpenSession(Guid.NewGuid(), path, new DatabaseHolder(locked.OwnerPid, locked.OwnerMachine, locked.StartedAt), []);

        Assert.Multiple(() =>
        {
            Assert.That(session.IsPaused, Is.True);
            Assert.That(session.Engine, Is.Null, "a paused session owns no engine — that is what makes the lock available to the holder");
            Assert.That(session.PausedBy, Is.Not.Null);
            Assert.That(session.PausedBy.Describe(), Does.Contain(Environment.ProcessId.ToString()),
                "the banner must name the holder — 'the database is busy' without a PID is not actionable");
        });
    }

    // ── AC4 · a paused session keeps the profiler and loses the database ─────────────────────────────────────────

    [Test]
    public async Task Pause_DropsDatabaseCapability_ButKeepsProfilerWhenAProfileIsAttached()
    {
        var fixture = FixtureDatabase.CreateOrReuse(_tempDir, force: true);
        var session = await OpenSessionAsync(fixture.TyphonFilePath);

        Assert.That(session.Capabilities, Does.Contain(SessionCapability.Database));

        _coordinator.Pause(session, holder: null, requestedSchemaDllPaths: []);

        Assert.Multiple(() =>
        {
            Assert.That(session.IsPaused, Is.True);
            Assert.That(session.Capabilities, Does.Not.Contain(SessionCapability.Database),
                "the database is gone, so advertising the capability would make every data panel fail instead of showing a paused state");
        });
    }

    [Test]
    public async Task PausedSession_StillListsItsCaptures()
    {
        // The whole point of pausing rather than closing: captures are FILES, so the profiler keeps working while the application owns the database.
        var fixture = FixtureDatabase.CreateOrReuse(_tempDir, force: true);
        var session = await OpenSessionAsync(fixture.TyphonFilePath);

        var profilings = TraceLocation.ProfilingsDirectoryOf(fixture.TyphonFilePath);
        Directory.CreateDirectory(profilings);

        // Two real captures, so "the list still works" is a claim about rows rather than about an empty list matching an empty list.
        WriteMinimalCapture(Path.Combine(profilings, "20260803-120000-000" + TraceLocation.TraceExtension), Guid.NewGuid());
        WriteMinimalCapture(Path.Combine(profilings, "20260803-130000-000" + TraceLocation.TraceExtension), Guid.NewGuid());

        var beforePause = ProfileCatalog.List(session);
        Assert.That(beforePause.Profiles, Has.Length.EqualTo(2), "precondition: the catalog must see both captures before the pause");

        _coordinator.Pause(session, holder: null, requestedSchemaDllPaths: []);
        var whilePaused = ProfileCatalog.List(session);

        Assert.Multiple(() =>
        {
            Assert.That(whilePaused, Is.Not.Null, "listing captures is a filesystem walk — it must not need an engine");
            Assert.That(whilePaused.Profiles, Has.Length.EqualTo(2), "every capture must still be listed while the database is released");
            Assert.That(whilePaused.ProfilingsDirectory, Is.EqualTo(beforePause.ProfilingsDirectory),
                "a paused session still knows its bundle, which is all the catalog ever needed");
        });
    }

    [Test]
    public void BelongsToDatabase_WithUnknownSessionId_DoesNotReject()
    {
        // A paused session passes Guid.Empty because it has no engine to ask. Comparing a real capture id against an unknown one would reject every capture
        // and blame database 00000000-… — a confident wrong answer from a check that had no information.
        var fixture = FixtureDatabase.CreateOrReuse(_tempDir, force: true);
        var profilings = TraceLocation.ProfilingsDirectoryOf(fixture.TyphonFilePath);
        Directory.CreateDirectory(profilings);

        var capture = Path.Combine(profilings, "20260803-120000-000" + TraceLocation.TraceExtension);
        WriteMinimalCapture(capture, Guid.NewGuid());

        Assert.That(ProfileCatalog.BelongsToDatabase(capture, Guid.Empty, out var reason), Is.True, reason);
    }

    [Test]
    public async Task PausedSession_SchemaProvider_DoesNotTouchTheDisposedEngine()
    {
        // StaticSchemaProvider memoises a LiveSchemaProvider over the engine. If pause left that memo in place, this call would reach into a disposed engine
        // — the failure would be an NRE or, worse, an answer read from freed state. With no profile attached the honest answer is null, which controllers
        // already map to the "schema unavailable" empty state; with one attached it becomes the capture's own trace-time schema.
        var fixture = FixtureDatabase.CreateOrReuse(_tempDir, force: true);
        var session = await OpenSessionAsync(fixture.TyphonFilePath);

        Assert.That(session.StaticSchemaProvider, Is.Not.Null, "precondition: a live session serves the database's schema");

        _coordinator.Pause(session, holder: null, requestedSchemaDllPaths: []);

        Assert.DoesNotThrow(() => _ = session.StaticSchemaProvider, "a paused session must not dereference the engine it just disposed");
        Assert.That(session.StaticSchemaProvider, Is.Null, "no engine and no attached profile means no schema to report");
    }

    // ── AC3 · the stale-cache hazard ─────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Pause_DropsCachedEntitySnapshots()
    {
        // DataBrowserService keys its snapshots on the OpenSession, and pausing is exactly the case where the session OUTLIVES the engine. Left in place,
        // those entries are served after resume as though nothing happened — the pre-pause entity list at the pre-pause revision, after the application has
        // written to the database. Wrong data that looks right is the worst thing this feature could ship.
        var fixture = FixtureDatabase.CreateOrReuse(_tempDir, force: true);
        var session = await OpenSessionAsync(fixture.TyphonFilePath);

        var archetypeId = FirstArchetypeIdWithEntities(session);
        Assume.That(archetypeId, Is.Not.Null, "fixture must contain at least one populated archetype for this test to mean anything");

        _dataBrowser.GetEntityPage(session.Id, archetypeId, 0, 10);
        Assert.That(_dataBrowser.HasCachedSnapshots(session), Is.True, "precondition: browsing must populate the cache, or the test proves nothing");

        _coordinator.Pause(session, holder: null, requestedSchemaDllPaths: []);

        Assert.That(_dataBrowser.HasCachedSnapshots(session), Is.False,
            "pausing must drop the session's snapshots — they belong to an engine that no longer exists");
    }

    [Test]
    public async Task PausedSession_RefusesDataBrowsing_WithARetryableConflict()
    {
        var fixture = FixtureDatabase.CreateOrReuse(_tempDir, force: true);
        var session = await OpenSessionAsync(fixture.TyphonFilePath);
        var archetypeId = FirstArchetypeIdWithEntities(session);
        Assume.That(archetypeId, Is.Not.Null);

        _coordinator.Pause(session, holder: null, requestedSchemaDllPaths: []);

        // 409, not 400: this resolves itself when the holder exits, so the client should retry rather than treat it as a permanent wrong-kind error.
        var ex = Assert.Throws<WorkbenchException>(() => _dataBrowser.GetEntityPage(session.Id, archetypeId, 0, 10));
        Assert.Multiple(() =>
        {
            Assert.That(ex.StatusCode, Is.EqualTo(409));
            Assert.That(ex.ErrorCode, Is.EqualTo("database_paused"));
        });
    }

    // ── AC6 · auto-resume ────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task PausedSession_ResumesAutomatically_OnceTheHolderReleases()
    {
        var fixture = FixtureDatabase.CreateOrReuse(_tempDir, force: true);
        var session = await OpenSessionAsync(fixture.TyphonFilePath);

        _coordinator.Pause(session, holder: null, requestedSchemaDllPaths: []);
        Assert.That(session.IsPaused, Is.True);

        // Nothing holds the database now, so the next poll must reopen it. Waiting on the observable state rather than sleeping a fixed interval keeps this
        // deterministic regardless of how the poll and the watcher interleave.
        var resumed = await WaitUntilAsync(() => !session.IsPaused, TimeSpan.FromSeconds(10));

        Assert.Multiple(() =>
        {
            Assert.That(resumed, Is.True, "the coordinator must reopen a database that is no longer held");
            Assert.That(session.Engine, Is.Not.Null);
            Assert.That(session.PausedBy, Is.Null, "a resumed session has no holder to name");
            Assert.That(session.Capabilities, Does.Contain(SessionCapability.Database));
            Assert.That(_dataBrowser.HasCachedSnapshots(session), Is.False, "resume must not carry pre-pause snapshots back in");
        });
    }

    [Test]
    public async Task PausedSession_StaysPaused_WhileTheDatabaseIsStillHeld()
    {
        var path = Path.Combine(_tempDir, "contended.typhon");
        var session = await OpenSessionAsync(path);

        _coordinator.Pause(session, holder: null, requestedSchemaDllPaths: []);

        // Somebody else takes it during the paused window — the design's "third process grabs the lock" case. Resuming into that would fail the open; staying
        // paused with profiles usable is the correct degradation.
        using var interloper = await EngineLifecycle.OpenAsync(path);

        var resumed = await WaitUntilAsync(() => !session.IsPaused, TimeSpan.FromSeconds(3));
        Assert.That(resumed, Is.False, "a database held by a live process must not be resumed into");
        Assert.That(session.IsPaused, Is.True);
    }

    [Test]
    public async Task SessionRemovedWhileResuming_DoesNotStrandAnEngineHoldingTheDatabase()
    {
        // The window is real: opening a database takes hundreds of milliseconds (schema load, archetype init, WAL
        // recovery), and closing a paused session during it is an ordinary user action. If the coordinator handed the
        // freshly-opened engine to a session that had already been disposed, nothing would hold a reference to that
        // engine — so nothing would ever dispose it, and its file handle plus db.lock would be held until the Workbench
        // process exited. The application this whole feature exists to unblock could then never reopen its own database.
        //
        // The proof is that the database can be opened again afterwards: a stranded engine makes that impossible.
        var fixture = FixtureDatabase.CreateOrReuse(_tempDir, force: true);
        var session = await OpenSessionAsync(fixture.TyphonFilePath);

        _coordinator.Pause(session, holder: null, requestedSchemaDllPaths: []);
        _sessions.Remove(session.Id); // races the poll's in-flight resume

        // Give the coordinator several poll windows to do the wrong thing if it is going to.
        await Task.Delay(3000);

        Assert.DoesNotThrowAsync(async () =>
        {
            using var reopened = await EngineLifecycle.OpenAsync(fixture.TyphonFilePath);
            Assert.That(reopened.Engine, Is.Not.Null);
        }, "a resume that lost the race must dispose its engine — otherwise the database stays locked for the process lifetime");
    }

    // ── AC17 · the holder keeps the promise its lock file makes ──────────────────────────────────────────────────

    [Test]
    public async Task ALiveSession_YieldsTheDatabase_WhenAClaimAppears()
    {
        // The Workbench writes `yieldable: true` into every lock it takes, which tells an application starting later
        // "ask and I will let go". This is the half that makes that true. Without it the advertisement is a lie that
        // costs the application a wait before failing exactly as it would have.
        var fixture = FixtureDatabase.CreateOrReuse(_tempDir, force: true);
        var session = await OpenSessionAsync(fixture.TyphonFilePath);
        _coordinator.TrackLiveSession(session, []);

        Assert.That(session.IsPaused, Is.False, "precondition: the session must actually hold the database");
        Assert.That(DatabaseLockFile.TryReadLock(fixture.TyphonFilePath, out var info), Is.True);
        Assert.That(info.Yieldable, Is.True, "the Workbench must advertise that it will yield, or no claimant will ever ask");

        DatabaseLockFile.WriteRequest(fixture.TyphonFilePath);

        var yielded = await WaitUntilAsync(() => session.IsPaused, TimeSpan.FromSeconds(30));

        // The lock is waited for, not sampled: IsPaused is set by Pause() while db.lock survives until the engine being
        // disposed releases the file. The two are effects of one yield, not one event, and reading the file off the flag's
        // timing is the race that wedged this test on the CI runner.
        var lockReleased = await WaitUntilAsync(() => !DatabaseLockFile.Exists(fixture.TyphonFilePath), TimeSpan.FromSeconds(30));

        Assert.Multiple(() =>
        {
            Assert.That(yielded, Is.True, "a claim on a yieldable database must be honoured");
            Assert.That(session.PausedBy?.Pid, Is.EqualTo(Environment.ProcessId), "the banner must name the process that asked");
            Assert.That(lockReleased, Is.True, "yielding means dropping the lock, not just the engine");
        });
    }

    [Test]
    public async Task AYieldedSession_DoesNotRetakeTheDatabase_WhileTheClaimIsStillInFlight()
    {
        // The window between "holder released" and "claimant acquired" is exactly when a holder polling for its turn
        // back would steal the database from the process it just stepped aside for. The request file is what closes it:
        // its presence means a claim is in flight, and only the claimant retires it.
        var fixture = FixtureDatabase.CreateOrReuse(_tempDir, force: true);
        var session = await OpenSessionAsync(fixture.TyphonFilePath);
        _coordinator.TrackLiveSession(session, []);

        DatabaseLockFile.WriteRequest(fixture.TyphonFilePath);
        Assert.That(await WaitUntilAsync(() => session.IsPaused, TimeSpan.FromSeconds(10)), Is.True);

        // The claimant has not acquired yet — no lock file, request still standing.
        var resumedTooEarly = await WaitUntilAsync(() => !session.IsPaused, TimeSpan.FromSeconds(3));
        Assert.That(resumedTooEarly, Is.False, "the holder must not re-take a database it has been asked to release until the claim clears");

        // The claimant acquires and later exits: request retired, lock gone.
        DatabaseLockFile.DeleteRequest(fixture.TyphonFilePath);

        Assert.That(await WaitUntilAsync(() => !session.IsPaused, TimeSpan.FromSeconds(15)), Is.True,
            "once the claim clears and the database is free, the session must come back on its own");
    }

    [Test]
    public async Task AnAbandonedClaim_DoesNotPinTheSessionOutOfItsDatabase()
    {
        // "Claimant dies after writing the request", from the design's failure table. A request that nobody will ever
        // retire would otherwise keep the Workbench paused for as long as it ran.
        var fixture = FixtureDatabase.CreateOrReuse(_tempDir, force: true);
        var session = await OpenSessionAsync(fixture.TyphonFilePath);
        _coordinator.TrackLiveSession(session, []);

        var requestPath = DatabaseLockFile.RequestPathFor(fixture.TyphonFilePath);
        File.WriteAllText(requestPath,
            $"{{\"pid\":{int.MaxValue - 1},\"machineName\":\"{Environment.MachineName}\",\"requestedAt\":\"{DateTimeOffset.UtcNow:o}\"}}");

        // A dead claimant's request is retired on sight, so the session never even yields — and if it did, it recovers.
        var stuck = await WaitUntilAsync(() => session.IsPaused && File.Exists(requestPath), TimeSpan.FromSeconds(3));
        Assert.That(stuck, Is.False, "an orphaned claim must not hold the session out of its own database");
        Assert.That(File.Exists(requestPath), Is.False, "the orphan must be removed — nobody else will ever clean it up");
    }

    [Test]
    public async Task AResumedSession_YieldsAgain_WhenASecondClaimAppears()
    {
        // The dev loop is a LOOP. An application closes its database and reopens it — ShardLab's "durability: close &
        // reopen" chapter does precisely that — so the handoff has to work more than once. It did not: resuming used to
        // Forget() the watch, which left a LIVE session holding a fresh lock that still advertised `yieldable: true`
        // with nobody watching for claims. The reopen then published its request and waited out the entire
        // LockHandoffTimeout against a watcher that no longer existed, before failing exactly as it would have anyway.
        //
        // The first handoff passing is what made this invisible: every existing yield test drives a session that was
        // tracked at creation and never resumed, so all three of them pass with the bug in place.
        var fixture = FixtureDatabase.CreateOrReuse(_tempDir, force: true);
        var session = await OpenSessionAsync(fixture.TyphonFilePath);
        _coordinator.TrackLiveSession(session, []);

        // Deadlines are deliberately generous. This test is the only one here that drives the protocol through THREE
        // state changes, each gated on a 1-second poll and one of them on a real engine open — so its wall-clock cost
        // scales with how loaded the machine is, and under a full-suite run on a contended CI box the original 10s/15s
        // budgets were not always enough. Raising them weakens no assertion: every one of these is a claim about what
        // the protocol EVENTUALLY does, and the poll interval is an implementation detail the test must not encode.
        var deadline = TimeSpan.FromSeconds(45);

        // Round 1 — the application starts and asks for the database.
        DatabaseLockFile.WriteRequest(fixture.TyphonFilePath);
        Assert.That(await WaitUntilAsync(() => session.IsPaused, deadline), Is.True,
            "precondition: the first handoff must work, or this test is proving nothing about the second");

        // The application exits: its claim is retired and the database is free again, so the session comes back.
        DatabaseLockFile.DeleteRequest(fixture.TyphonFilePath);
        Assert.That(await WaitUntilAsync(() => !session.IsPaused, deadline), Is.True, "the session must resume once its database is free");
        Assert.That(DatabaseLockFile.TryReadLock(fixture.TyphonFilePath, out var info), Is.True, "a resumed session holds the lock again");
        Assert.That(info.Yieldable, Is.True, "the resumed lock re-advertises the promise — which is why it must still be kept");

        // Round 2 — the application starts again. This is the case that regressed.
        DatabaseLockFile.WriteRequest(fixture.TyphonFilePath);

        var yieldedAgain = await WaitUntilAsync(() => session.IsPaused, deadline);

        // Wait for the lock too, rather than reading it the instant IsPaused flips. They are two effects of one yield and
        // they are NOT simultaneous: Pause() sets the flag, and db.lock goes when the engine it disposes finishes letting
        // go of the file. Asserting the file state off the flag's timing is a race the test invents — it passed on Windows
        // because dispose happened to win, and failed one Linux run in three because there it sometimes does not.
        var lockReleased = await WaitUntilAsync(() => !DatabaseLockFile.Exists(fixture.TyphonFilePath), deadline);

        Assert.Multiple(() =>
        {
            Assert.That(yieldedAgain, Is.True, "a resumed session must honour a second claim — the promise is re-made every time the lock is re-taken");
            Assert.That(lockReleased, Is.True, "yielding means dropping the lock, not just the engine");
        });

        // Leave the coordinator quiescent. Every other test here ends with its session either live or resumed, so its
        // watch goes idle; this one deliberately ends mid-protocol — paused, with a claim still standing — which leaves
        // the poll actively looking for its turn back while TearDown pulls the temp directory out from under it. That
        // cost the NEXT test its resume in a full-suite Release run (and only there: the fixture passes alone, because
        // nothing follows it). Retiring the claim and the session is the test cleaning up its own protocol state, not a
        // workaround — SessionRemoved is the same edge production uses to stop watching a session that has gone away.
        DatabaseLockFile.DeleteRequest(fixture.TyphonFilePath);
        _sessions.Remove(session.Id);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Polls a predicate to a deadline. Preferred over a fixed sleep: the poll interval is an implementation detail this test must not encode.</summary>
    private static async Task<bool> WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return true;
            }
            await Task.Delay(50);
        }
        return predicate();
    }

    /// <summary>
    /// First registered archetype the Data Browser can actually page — probed rather than derived, because the storage descriptors do not carry an archetype
    /// id and the registry's ids are assigned at registration time, not fixed by the fixture.
    /// </summary>
    private string FirstArchetypeIdWithEntities(OpenSession session)
    {
        for (ushort id = 0; id < 256; id++)
        {
            if (ArchetypeRegistry.GetMetadata(id) == null)
            {
                continue;
            }
            try
            {
                if (_dataBrowser.GetEntityPage(session.Id, id.ToString(), 0, 1).TotalCount > 0)
                {
                    return id.ToString();
                }
            }
            catch
            {
                // Not pageable in this session (unregistered component, no storage) — keep looking.
            }
        }
        return null;
    }

    /// <summary>Writes a header-only capture — enough for the catalog's single-header read, which is all these tests exercise.</summary>
    private static void WriteMinimalCapture(string path, Guid databaseId)
    {
        var header = new Typhon.Profiler.TraceFileHeader
        {
            Magic = Typhon.Profiler.TraceFileHeader.MagicValue,
            Version = Typhon.Profiler.TraceFileHeader.CurrentVersion,
            DatabaseId = databaseId,
        };
        header.SetDatabaseName("probe");

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new Typhon.Profiler.TraceFileWriter(stream);
        writer.WriteHeader(in header);
    }
}
