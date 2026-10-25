using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NUnit.Framework;
using Typhon.Workbench.Backups;
using Typhon.Workbench.Dtos.Backups;
using Typhon.Workbench.Security;
using Typhon.Workbench.Tests.Fixtures;
using static Typhon.Workbench.Tests.Backups.BackupsReadEndpointTests;

namespace Typhon.Workbench.Tests.Backups;

/// <summary>
/// #1417 task 4: the Backups view's jobs against a real store — every route of Back up now (AC-W3), verify's report (AC-W4), a drill in its child process
/// (AC-W5), retention bound to its preview (AC-W6), restore bound to its preview and settled, its target guarded (AC-W7), and jobs that outlive the client
/// that started them (AC-W8). The long calls pass through a gate a test holds, never a timer.
/// </summary>
[TestFixture]
[NonParallelizable] // opens real engines (the process-global ArchetypeRegistry, #554)
public sealed class BackupsJobEndpointTests
{
    private WorkbenchFactory _factory;
    private WebApplicationFactory<Program> _host;
    private GatedBackupOperations _ops;
    private HttpClient _client;
    private string _bundle;
    private string _store;

    [SetUp]
    public void SetUp()
    {
        _factory = new WorkbenchFactory();
        _ops = new GatedBackupOperations();
        _host = _factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<IBackupOperations>();
            s.AddSingleton<IBackupOperations>(_ops);
        }));
        _client = NewClient();
        _bundle = Path.Combine(_factory.DemoDirectory, "db", "plain.typhon");
        _store = Path.Combine(_factory.DemoDirectory, "store");
    }

    [TearDown]
    public void TearDown()
    {
        _ops.Release();
        _host.Services.GetRequiredService<BackupJobRegistry>().CancelAll(TimeSpan.FromSeconds(10));
        _client.Dispose();
        _host.Dispose();
        _factory.Dispose();
    }

    private HttpClient NewClient()
    {
        var client = _host.CreateClient();
        client.DefaultRequestHeaders.Add(BootstrapTokenGate.HeaderName, _host.Services.GetRequiredService<BootstrapTokenGate>().Token);
        return client;
    }

    private Task<BackupJobDto> Start(string kind, object body) => Post<BackupJobDto>(_client, $"/api/backups/jobs/{kind}", body);

    /// <summary>Follows the job's stream to its terminal frame, calling <paramref name="onProgress"/> for each progress frame.</summary>
    private async Task<BackupJobDto> Terminal(Guid jobId, Action<BackupJobDto> onProgress = null, CancellationToken deadline = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(deadline);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        using var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/backups/jobs/{jobId}/stream"),
            HttpCompletionOption.ResponseHeadersRead, cts.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));
        while (await SseFrameReader.ReadFrameAsync(reader, cts.Token) is { } frame)
        {
            var dto = JsonSerializer.Deserialize<BackupJobDto>(frame.Data, Json);
            if (BackupJobStates.IsTerminal(dto.State))
            {
                return dto;
            }

            onProgress?.Invoke(dto);
        }

        throw new InvalidOperationException("The job's stream ended without a terminal frame.");
    }

    private static void Done(BackupJobDto job) =>
        Assert.That(job.State, Is.EqualTo(BackupJobStates.Done), job.Error == null ? job.State : $"{job.Error.Category}: {job.Error.Message}");

    private static BackupPointRefDto Latest => new("latest", null, null, null, null);

    private static BackupPointRefDto Point(uint id) => new("id", id, null, null, null);

    // ── Back up now (AC-W3) ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    [CancelAfter(60_000)]
    public async Task BackUpNow_OfAClosedBundle_IsOffline()
    {
        await MakeBundleWithTwoPoints(_bundle, _store);

        var started = await Start("create", new BackupCreateRequestDto(_store, _bundle, true));
        Assert.That(started.Route, Is.EqualTo("offline"));
        var job = await Terminal(started.JobId);

        Done(job);
        Assert.That((job.Create.Point.Offline, job.Create.Point.Kind), Is.EqualTo((true, "Full")));
        Assert.That((job.Completed, job.Total, job.Unit), Is.EqualTo((job.Create.Point.ImagePages, (long)job.Create.Point.ImagePages, "pages")));
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task BackUpNow_OfASessionsDatabase_IsOnline_AndTheSessionKeepsAnswering()
    {
        await MakeBundleWithTwoPoints(_bundle, _store);
        var session = await OpenSession(_client, _bundle);
        _ops.Close();

        var started = await Start("create", new BackupCreateRequestDto(_store, _bundle, false));
        Assert.That(started.Route, Is.EqualTo("inProcess"));
        Assert.That(_ops.Entered.Wait(TimeSpan.FromSeconds(10)), Is.True);

        // The capture is under way (held at its gate): the session answers.
        Assert.That(await SessionAnswers(session.SessionId), Is.EqualTo(HttpStatusCode.OK));

        _ops.Release();
        var job = await Terminal(started.JobId);
        Done(job);
        Assert.That(job.Create.Point.Offline, Is.False);
        Assert.That(job.Total, Is.Zero, "the Workbench's engine reports no capture progress: indeterminate, never a guess");
        Assert.That(await SessionAnswers(session.SessionId), Is.EqualTo(HttpStatusCode.OK));
    }

    private async Task<HttpStatusCode> SessionAnswers(Guid sessionId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/sessions/{sessionId}");
        request.Headers.Add("X-Session-Token", sessionId.ToString());
        return (await _client.SendAsync(request)).StatusCode;
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task BackUpNow_OfAForeignEngine_IsARequest_WhoseIdItsStatusEchoes()
    {
        await MakeBundleWithTwoPoints(_bundle, _store);
        using var engine = DatabaseEngine.Open(_bundle, o => o.Backup(new BackupOptions
        {
            StorePath = _store, Interval = TimeSpan.Zero, Retention = null, ManualTickForTest = true,
        }));
        var scheduler = engine.BackupScheduler;
        scheduler.TickForTest();

        var started = await Start("create", new BackupCreateRequestDto(_store, _bundle, true));
        Assert.That(started.Route, Is.EqualTo("request"));
        var served = false;
        var job = await Terminal(started.JobId, dto =>
        {
            if (served || dto.Phase != "requested")
            {
                return;
            }

            // The request is written (the job reports "requested" after it): the engine's tick serves it, the tick after republishes the status.
            served = true;
            using var ended = new SemaphoreSlim(0);
            scheduler.JobEndedProbe = () => ended.Release();
            scheduler.TickForTest();
            Assert.That(ended.Wait(TimeSpan.FromSeconds(30)), Is.True, "the engine's backup job ended");
            scheduler.JobEndedProbe = null;
            scheduler.TickForTest();
        });

        Done(job);
        Assert.That(job.Create.RequestId, Is.Not.Null);
        Assert.That(BackupOperations.ReadStatus(_bundle).Status.LastRequestId, Is.EqualTo(job.Create.RequestId), "the engine echoed this request");
        Assert.That(job.Create.Point.Offline, Is.False);
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task BackUpNow_ByRequest_WhoseJobTheEngineDefers_EndsAsDeferred_WithoutWaitingOutTheTimeout()
    {
        await MakeBundleWithTwoPoints(_bundle, _store);
        using var engine = DatabaseEngine.Open(_bundle, o => o.Backup(new BackupOptions
        {
            StorePath = _store, Interval = TimeSpan.Zero, Retention = null, ManualTickForTest = true,
        }));
        var scheduler = engine.BackupScheduler;
        scheduler.TickForTest();

        // Another process holds the store: the engine's capture is deferred (BackupStoreLocked), which moves neither its point nor its failure count.
        using var holder = Typhon.Engine.Internals.BackupStore.OpenForMaintenance(_store, "test holds the store");
        var started = await Start("create", new BackupCreateRequestDto(_store, _bundle, true));
        Assert.That(started.Route, Is.EqualTo("request"));
        var served = false;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var job = await Terminal(started.JobId, dto =>
        {
            if (served || dto.Phase != "requested")
            {
                return;
            }

            served = true;
            using var ended = new SemaphoreSlim(0);
            scheduler.JobEndedProbe = () => ended.Release();
            scheduler.TickForTest();
            Assert.That(ended.Wait(TimeSpan.FromSeconds(30)), Is.True, "the engine's backup job ended");
            scheduler.JobEndedProbe = null;
            scheduler.TickForTest();
        }, cts.Token);

        Assert.That(job.State, Is.EqualTo(BackupJobStates.Error));
        Assert.That(job.Error.Message, Does.Contain("deferred"));
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task BackUpNow_WithNoBundle_IsRefused_WithItsReason()
    {
        var response = await _client.PostAsJsonAsync("/api/backups/jobs/create", new BackupCreateRequestDto(_store, null, false));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("No database bundle"));
    }

    // ── Verify, drill, compact (AC-W4, AC-W5) ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    [CancelAfter(60_000)]
    public async Task VerifyFull_OfAFlippedPackByte_IsDamaged_WithTheCliFinding()
    {
        await MakeBundleWithTwoPoints(_bundle, _store);
        var pack = Directory.GetFiles(Path.Combine(_store, "packs"), "00000001-*.tpack").Single();
        var bytes = await File.ReadAllBytesAsync(pack);
        bytes[bytes.Length / 2] ^= 0x5A;
        await File.WriteAllBytesAsync(pack, bytes);

        var job = await Terminal((await Start("verify", new BackupVerifyRequestDto(_store, null, "full"))).JobId);

        Done(job);
        var facade = BackupOperations.Verify(_store, BackupVerifyLevel.Full);
        Assert.That(job.Verify.Verdict, Is.EqualTo(facade.Verdict.ToString()));
        Assert.That(job.Verify.Verdict, Is.AnyOf("Damaged", "NoRestorablePoint"));
        Assert.That(job.Verify.Packs.SelectMany(p => p.Findings).Select(f => f.Code),
            Is.EquivalentTo(facade.Packs.SelectMany(p => p.Findings).Select(f => f.Code)));
        Assert.That((job.Phase, job.Completed, job.Total), Is.EqualTo(("packs", 2L, 2L)), "progress reaches its total");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task ADrill_RunsInAChildProcess_AndIsRecorded()
    {
        await MakeBundleWithTwoPoints(_bundle, _store);
        var scratch = Path.Combine(_factory.DemoDirectory, "scratch");

        var job = await Terminal((await Start("drill", new BackupDrillRequestDto(_store, Point(2), scratch, _bundle))).JobId);

        Done(job);
        Assert.That(job.Drill.Outcome, Is.EqualTo("Passed"), job.Drill.Error);
        Assert.That(job.Drill.Point.BackupId, Is.EqualTo(2u));
        Assert.That(File.Exists(job.Drill.RecordFile), Is.True, "the drill's record is in the store's drills/");
        Assert.That(BackupOperations.List(_store).Drills.ContainsKey(2), Is.True);
        Assert.That(Directory.Exists(scratch) ? Directory.GetFileSystemEntries(scratch) : [], Is.Empty, "the scratch is gone");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Compact_OfAnIncremental_WritesItsBase()
    {
        await MakeBundleWithTwoPoints(_bundle, _store);
        Assert.That(BackupOperations.List(_store).Points.Single(p => p.BackupId == 2).Kind, Is.EqualTo(BackupPointKind.Incremental),
            "premise: point #2 rests on #1");

        var job = await Terminal((await Start("compact", new BackupCompactRequestDto(_store, Point(2)))).JobId);

        Done(job);
        Assert.That((job.Compact.AlreadyBase, job.Compact.Point.Kind), Is.EqualTo((false, "Compacted")));
        Assert.That(File.Exists(Path.Combine(_store, "packs", "00000002-C.tpack")), Is.True);
    }

    // ── Retention (AC-W6) ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    [CancelAfter(60_000)]
    public async Task Retention_AppliesTheReviewedPlan_AndAPointPublishedSince_IsPlanChanged_NothingDeleted()
    {
        await MakeBundleWithTwoPoints(_bundle, _store);
        // Keep two: the reviewed plan deletes nothing. A third point published after the review makes the same policy compact #2 and delete #1's pack,
        // which the operator never saw.
        var policy = new BackupRetentionPolicyDto(0, 2, 0, 1, 0, false);
        var plan = await Post<BackupRetentionPlanDto>(_client, "/api/backups/retention/plan", new BackupRetentionPlanRequestDto(_store, policy));
        Assert.That(plan.PackDeletions, Is.Empty, "premise: the reviewed plan deletes nothing");

        BackupOperations.Create(_bundle, _store, new BackupCreateOptions { Full = true });
        var before = Directory.GetFiles(Path.Combine(_store, "packs")).Select(Path.GetFileName).OrderBy(f => f).ToList();

        var job = await Terminal((await Start("retention", new BackupRetentionApplyRequestDto(_store, plan.Fingerprint))).JobId);

        Assert.That(job.State, Is.EqualTo(BackupJobStates.Error));
        Assert.That(job.Error.Category, Is.EqualTo("plan_changed"));
        Assert.That(Directory.GetFiles(Path.Combine(_store, "packs")).Select(Path.GetFileName).OrderBy(f => f), Is.EqualTo(before), "nothing deleted");

        var fresh = await Post<BackupRetentionPlanDto>(_client, "/api/backups/retention/plan", new BackupRetentionPlanRequestDto(_store, policy));
        Assert.That(fresh.PackDeletions, Is.Not.Empty, "premise: the plan reviewed now deletes something");
        var applied = await Terminal((await Start("retention", new BackupRetentionApplyRequestDto(_store, fresh.Fingerprint))).JobId);
        Done(applied);
        Assert.That(applied.Retention.Deleted, Is.EquivalentTo(fresh.PackDeletions));
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Retention_WithoutAPlanThisWorkbenchServed_IsRefused()
    {
        var response = await _client.PostAsJsonAsync("/api/backups/jobs/retention", new BackupRetentionApplyRequestDto(_store, "not-a-plan"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("plan_changed"));
    }

    // ── Restore (AC-W7) ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private BackupRestoreRequestDto Restore(string target, string fingerprint, BackupPointRefDto point = null) =>
        new(_store, point ?? Latest, target, false, false, false, _bundle, fingerprint);

    private async Task<BackupRestorePlanDto> PlanRestore(string target, BackupPointRefDto point = null) =>
        await Post<BackupRestorePlanDto>(_client, "/api/backups/restore/plan", Restore(target, null, point));

    [Test]
    [CancelAfter(60_000)]
    public async Task ARestore_IsSettled_AndOpensAsAReadySession()
    {
        await MakeBundleWithTwoPoints(_bundle, _store);
        var target = Path.Combine(_factory.DemoDirectory, "restored");
        var plan = await PlanRestore(target);

        var job = await Terminal((await Start("restore", Restore(target, plan.Fingerprint))).JobId);

        Done(job);
        Assert.That(job.Restore.Settle, Is.Not.Null);
        Assert.That((job.Restore.Settle.Ran, job.Restore.Settle.Error, job.Restore.Settle.WithSchema), Is.EqualTo((true, (string)null, false)));
        Assert.That(job.Restore.Target, Is.EqualTo(plan.ResolvedTarget));
        Assert.That(File.Exists(Path.Combine(job.Restore.Target, "restore.json")), Is.False, "the settle's open adopted the restore");

        var session = await OpenSession(_client, job.Restore.Target);
        Assert.That(session.State, Is.EqualTo("Ready"));
    }

    [Test]
    [CancelAfter(120_000)]
    public async Task ASchemadRestore_SettlesInAChildProcess_WithTheSessionsSchema()
    {
        // A bundle of the application's schema (the SWG sample's), backed up closed, then opened in this Workbench: its session loads the schema.
        var fixture = Typhon.Workbench.Fixtures.FixtureDatabase.CreateOrReuse(Path.Combine(_factory.DemoDirectory, "fixture"), force: true,
            Typhon.Workbench.Fixtures.FixtureConfig.Default with
            {
                ResourceTypeCount = 6, GuildCount = 3, RecipeCount = 5, PlayerCount = 20, DepositCount = 10, HarvesterCount = 5, FactoryCount = 5,
                ItemCount = 20,
            });
        BackupOperations.Create(fixture.TyphonFilePath, _store);
        var session = await OpenSession(_client, fixture.TyphonFilePath);
        Assert.That(session.SchemaDllPaths, Is.Not.Empty, "premise: the session loaded the application's schema");

        var target = Path.Combine(_factory.DemoDirectory, "restored");
        var request = new BackupRestoreRequestDto(_store, Latest, target, false, false, false, fixture.TyphonFilePath, null);
        var plan = await Post<BackupRestorePlanDto>(_client, "/api/backups/restore/plan", request);
        var job = await Terminal((await Start("restore", request with { PlanFingerprint = plan.Fingerprint })).JobId);

        Done(job);
        Assert.That((job.Restore.Settle.Ran, job.Restore.Settle.Error, job.Restore.Settle.WithSchema), Is.EqualTo((true, (string)null, true)));
        Assert.That(_ops.LastSettle, Is.Not.Null);
        Assert.That(_ops.LastSettle.ProcessId, Is.Not.EqualTo(Environment.ProcessId),
            "WB-13: the schema'd settle opens its engine in a child process, never beside the live session");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task ASettleThatRanNoRecovery_OnAnImageThatNeedsOne_IsAFailedSettle()
    {
        await MakeBundleWithTwoPoints(_bundle, _store);
        var target = Path.Combine(_factory.DemoDirectory, "restored");
        var plan = await PlanRestore(target);

        // What no engine open produces today, and WB-10 still guards against: an open that had to take the crash path and did not.
        _ops.SettleOverride = r => r with { RecoveryRequired = true, CrashRecoveryRan = false };
        var job = await Terminal((await Start("restore", Restore(target, plan.Fingerprint))).JobId);

        Done(job);
        Assert.That(job.Restore.Settle.Ran, Is.True);
        Assert.That(job.Restore.Settle.CrashRecoveryRan, Is.False);
        Assert.That(job.Restore.Settle.Error, Does.Contain("no crash recovery"), "not a settle: the bundle holds the image as captured");
        Assert.That(job.Restore.Settle.NeedsSchema, Is.False);
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task ADrillOnAnotherStore_WhileOneRuns_IsBusy_AndNotTheStoresOwnJob()
    {
        await MakeBundleWithTwoPoints(_bundle, _store);
        var registry = _host.Services.GetRequiredService<BackupJobRegistry>();
        using var gate = new ManualResetEventSlim();
        var drilling = registry.TryStart(new BackupJobSpec(BackupJobKinds.Drill, _store), ctx => gate.Wait(ctx.Token));
        Assert.That(drilling.Started, Is.True);

        var other = Path.Combine(_factory.DemoDirectory, "other-store");
        var response = await _client.PostAsJsonAsync("/api/backups/jobs/drill", new BackupDrillRequestDto(other, Latest, null, null));
        gate.Set();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.That(problem.GetProperty("title").GetString(), Is.EqualTo("busy"));
        Assert.That(problem.GetProperty("runningJobId").GetGuid(), Is.EqualTo(drilling.Job.Id));
        Assert.That(problem.GetProperty("sameStore").GetBoolean(), Is.False, "the client must not attach this store's card to another store's drill");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task APreview_WhoseStoreCannotSayWhatIsLost_SaysUnknown_NotNothing()
    {
        await MakeBundleWithTwoPoints(_bundle, _store);
        _ops.ArchiveFault = new IOException("the share went away");

        var plan = await PlanRestore(Path.Combine(_factory.DemoDirectory, "restored"));

        Assert.That(plan.CanRestore, Is.True, "the plan stands; only what is lost after it is unknown");
        Assert.That((object)plan.NewestKnownLsn, Is.Null);
        Assert.That((object)plan.LostLsns, Is.Null);
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task ARestore_UnderAStaleFingerprint_IsPlanChanged_AndWritesNothing()
    {
        await MakeBundleWithTwoPoints(_bundle, _store);
        var target = Path.Combine(_factory.DemoDirectory, "restored");
        var plan = await PlanRestore(target);
        BackupOperations.Create(_bundle, _store, new BackupCreateOptions { Full = true });   // "latest" now names another point

        var response = await _client.PostAsJsonAsync("/api/backups/jobs/restore", Restore(target, plan.Fingerprint));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("plan_changed"));
        Assert.That(Directory.Exists(target), Is.False, "nothing written");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task WhileARestoreRuns_ItsTargetCannotBeOpened_AndACancelBeforeThePublish_LeavesNoTarget()
    {
        await MakeBundleWithTwoPoints(_bundle, _store);
        var target = Path.Combine(_factory.DemoDirectory, "restored");
        var plan = await PlanRestore(target);
        _ops.Close();

        var started = await Start("restore", Restore(target, plan.Fingerprint));
        Assert.That(_ops.Entered.Wait(TimeSpan.FromSeconds(10)), Is.True);

        var open = await _client.PostAsJsonAsync("/api/sessions/file", new Dtos.Sessions.CreateFileSessionRequest(plan.ResolvedTarget));
        Assert.That(open.StatusCode, Is.EqualTo(HttpStatusCode.Conflict), "WB-12: no session opens a bundle a restore is writing");
        Assert.That(await open.Content.ReadAsStringAsync(), Does.Contain(started.JobId.ToString()));

        Assert.That((await _client.DeleteAsync($"/api/backups/jobs/{started.JobId}")).StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        _ops.Release();
        var job = await Terminal(started.JobId);

        Assert.That(job.State, Is.EqualTo(BackupJobStates.Cancelled));
        Assert.That(Directory.Exists(plan.ResolvedTarget), Is.False, "cancelled before its publish: no target");
        Assert.That(Directory.Exists(target) ? Directory.GetFileSystemEntries(target, "*.restoring") : [], Is.Empty, "and no staging left");
    }

    // ── Jobs outlive their client (AC-W8) ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    [CancelAfter(60_000)]
    public async Task AJob_IsReattachedByAnotherClient_RunningThenDone_AndOnePerStore()
    {
        await MakeBundleWithTwoPoints(_bundle, _store);
        _ops.Close();
        var started = await Start("verify", new BackupVerifyRequestDto(_store, null, "quick"));
        Assert.That(_ops.Entered.Wait(TimeSpan.FromSeconds(10)), Is.True);

        var busy = await _client.PostAsJsonAsync("/api/backups/jobs/compact", new BackupCompactRequestDto(_store, Latest));
        Assert.That(busy.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        var problem = JsonDocument.Parse(await busy.Content.ReadAsStringAsync()).RootElement;
        Assert.That(problem.GetProperty("title").GetString(), Is.EqualTo("busy"));
        Assert.That(problem.GetProperty("runningJobId").GetGuid(), Is.EqualTo(started.JobId));

        using var other = NewClient();
        var running = await other.GetFromJsonAsync<List<BackupJobDto>>($"/api/backups/jobs?store={Uri.EscapeDataString(_store)}", Json);
        Assert.That(running.Single().JobId, Is.EqualTo(started.JobId));
        Assert.That(running.Single().State, Is.EqualTo(BackupJobStates.Running));

        _ops.Release();
        await Terminal(started.JobId);
        var listed = await other.GetFromJsonAsync<List<BackupJobDto>>($"/api/backups/jobs?store={Uri.EscapeDataString(_store)}", Json);
        Assert.That((listed.Single().State, listed.Single().Verify), Is.EqualTo((BackupJobStates.Done, (BackupVerifyReportDto)null)),
            "the list carries summaries; the result is the job's own GET");
        var after = await other.GetFromJsonAsync<BackupJobDto>($"/api/backups/jobs/{started.JobId}", Json);
        Done(after);
        Assert.That(after.Verify.Verdict, Is.EqualTo("Ok"), "the result is kept for whoever comes back");
        Assert.That((await other.GetAsync($"/api/backups/jobs/{Guid.NewGuid()}")).StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }
}
