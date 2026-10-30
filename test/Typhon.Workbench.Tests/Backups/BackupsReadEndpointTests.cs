using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NUnit.Framework;
using Typhon.Workbench.Dtos.Backups;
using Typhon.Workbench.Dtos.Sessions;
using Typhon.Workbench.Sessions;

namespace Typhon.Workbench.Tests.Backups;

/// <summary>
/// #1417 task 3: the Backups view's reads against a real store — status and list say what <c>BackupOperations</c> says (AC-W1, AC-W2), "Back up now"
/// takes the route each holder calls for (WB-4), a restore preview refuses a target a Workbench session holds (WB-12), and none of it answers without
/// the bootstrap token.
/// </summary>
[TestFixture]
public sealed class BackupsReadEndpointTests
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private WorkbenchFactory _factory;
    private HttpClient _client;
    private string _bundle;
    private string _store;

    [SetUp]
    public void SetUp()
    {
        _factory = new WorkbenchFactory();
        _client = _factory.CreateAuthenticatedClient();
        _bundle = Path.Combine(_factory.DemoDirectory, "db", "plain.typhon");
        _store = Path.Combine(_factory.DemoDirectory, "store");
    }

    [TearDown]
    public void TearDown()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    /// <summary>A closed schemaless database with an online point and an offline one after it.</summary>
    internal static async Task MakeBundleWithTwoPoints(string bundle, string store)
    {
        using (var lifecycle = await EngineLifecycle.OpenAsync(bundle))
        {
            await lifecycle.Engine.Backup.CreateAsync(store);
        }

        BackupOperations.Create(bundle, store);
    }

    internal static async Task<T> Post<T>(HttpClient client, string url, object body, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await client.PostAsJsonAsync(url, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.That(response.StatusCode, Is.EqualTo(expected), text);
        return JsonSerializer.Deserialize<T>(text, Json);
    }

    internal static async Task<SessionDto> OpenSession(HttpClient client, string bundle) =>
        await Post<SessionDto>(client, "/api/sessions/file", new CreateFileSessionRequest(bundle), HttpStatusCode.Created);

    private Task<BackupStatusDto> Status(string bundle = null, string store = null) =>
        Post<BackupStatusDto>(_client, "/api/backups/status", new BackupStatusRequestDto(store ?? _store, bundle ?? _bundle));

    [Test]
    [CancelAfter(30_000)]
    public async Task StatusAndList_SayWhatTheFacadeSays()
    {
        await MakeBundleWithTwoPoints(_bundle, _store);

        var status = await Status();
        var facade = BackupOperations.Status(_store);
        Assert.That((status.StoreStatus.Health, status.StoreStatus.Reason, status.StoreStatus.Points),
            Is.EqualTo((facade.Health.ToString(), facade.Reason, facade.Points)));
        Assert.That(status.StoreStatus.Latest.BackupId, Is.EqualTo(facade.Latest.BackupId));
        Assert.That(status.StoreError, Is.Null);
        Assert.That(status.Engine, Is.Null, "the Workbench's engines run without BackupOptions: no status file");

        var list = await Post<BackupListingDto>(_client, "/api/backups/list", new BackupListRequestDto(_store, true));
        var listing = BackupOperations.List(_store, computeRestorable: true);
        Assert.That(list.Points.Select(p => (p.BackupId, p.Kind, p.PackFile, p.Offline)),
            Is.EqualTo(listing.Points.Select(p => (p.BackupId, p.Kind.ToString(), p.PackFile, p.Offline))));
        Assert.That(list.Chains.Select(c => (c.HeadBackupId, c.Depth)), Is.EqualTo(listing.Chains.Select(c => (c.HeadBackupId, c.Depth))));
        Assert.That(list.RestorablePoints, Is.EqualTo(listing.RestorablePoints));
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task AStoreNotCreatedYet_IsSaid_NotFailed()
    {
        var status = await Status(store: Path.Combine(_factory.DemoDirectory, "no-store-yet"));
        Assert.That(status.StoreStatus, Is.Null);
        Assert.That(status.StoreError, Does.Contain("not a store yet"));

        var response = await _client.PostAsJsonAsync("/api/backups/list", new BackupListRequestDto(Path.Combine(_factory.DemoDirectory, "nope"), false));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task TheRoute_FollowsWhoHoldsTheDatabase()
    {
        await MakeBundleWithTwoPoints(_bundle, _store);
        Assert.That((await Status()).Route.Kind, Is.EqualTo("offline"), "nobody holds it");

        var session = await OpenSession(_client, _bundle);
        var live = await Status();
        Assert.That((live.Route.Kind, live.Route.SessionId), Is.EqualTo(("inProcess", (Guid?)session.SessionId)));
        await _client.DeleteAsync($"/api/sessions/{session.SessionId}");

        using (await EngineLifecycle.OpenAsync(_bundle))
        {
            var held = await Status();
            Assert.That(held.Route.Kind, Is.EqualTo("unavailable"), "another holder, with no backup configured");
            Assert.That(held.Route.Reason, Does.Contain("no engine with backup configured"));
        }

        var none = await Post<BackupStatusDto>(_client, "/api/backups/status", new BackupStatusRequestDto(_store, null));
        Assert.That(none.Route.Kind, Is.EqualTo("unavailable"));
        Assert.That(none.Route.Reason, Does.Contain("No database bundle"));
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task AnEngineWithBackupConfigured_IsAskedByRequest_IntoItsOwnStoreOnly()
    {
        await MakeBundleWithTwoPoints(_bundle, _store);
        using var engine = DatabaseEngine.Open(_bundle, o => o.Backup(new BackupOptions
        {
            StorePath = _store, Interval = TimeSpan.Zero, Retention = null, ManualTickForTest = true,
        }));
        engine.BackupScheduler.TickForTest();   // writes backup.status.json

        var status = await Status();
        Assert.That(status.Route.Kind, Is.EqualTo("request"));
        Assert.That(status.Engine, Is.Not.Null, "the bundle's engine status comes from its file");
        Assert.That((status.Engine.EngineLive, status.Engine.StorePath, status.Engine.Pid), Is.EqualTo((true, _store, Environment.ProcessId)));

        var elsewhere = await Status(store: Path.Combine(_factory.DemoDirectory, "other-store"));
        Assert.That(elsewhere.Route.Kind, Is.EqualTo("unavailable"));
        Assert.That(elsewhere.Route.Reason, Does.Contain(_store), "names the store a request would land in");
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task ARestorePreview_RefusesATargetASessionHolds_AndAFreeOneIsPlanned()
    {
        await MakeBundleWithTwoPoints(_bundle, _store);
        var session = await OpenSession(_client, _bundle);

        var held = await Post<BackupRestorePlanDto>(_client, "/api/backups/restore/plan",
            new BackupRestoreRequestDto(_store, new BackupPointRefDto("latest", null, null, null, null), Path.GetDirectoryName(_bundle), true, false,
                false, _bundle, null));
        Assert.That(held.CanRestore, Is.False);
        Assert.That(held.HeldBySessionId, Is.EqualTo(session.SessionId));
        Assert.That(held.RefusalMessage, Is.Not.Null);

        var free = await Post<BackupRestorePlanDto>(_client, "/api/backups/restore/plan",
            new BackupRestoreRequestDto(_store, new BackupPointRefDto("id", 1, null, null, null), Path.Combine(_factory.DemoDirectory, "restored"), false,
                false, false, _bundle, null));
        Assert.That(free.CanRestore, Is.True, free.RefusalMessage);
        Assert.That(free.Fingerprint, Is.Not.Empty);
        Assert.That(free.Chain, Is.EqualTo(new uint[] { 1 }));
        Assert.That(free.ResolvedTarget, Does.EndWith("plain.typhon"));
        Assert.That(free.NewestKnownLsn, Is.GreaterThanOrEqualTo(free.PointLsn));
        Assert.That(free.LostLsns, Is.EqualTo(free.NewestKnownLsn - free.PointLsn), "what point #2 holds past #1 is lost");
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task ARetentionPreview_RunsTheEnginesValidation()
    {
        await MakeBundleWithTwoPoints(_bundle, _store);

        var invalid = await _client.PostAsJsonAsync("/api/backups/retention/plan",
            new BackupRetentionPlanRequestDto(_store, new BackupRetentionPolicyDto(0, 1, 0, 0, 0, false)));
        Assert.That(invalid.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await invalid.Content.ReadAsStringAsync(), Does.Contain("MinKeep"), "the engine's own sentence");

        var plan = await Post<BackupRetentionPlanDto>(_client, "/api/backups/retention/plan",
            new BackupRetentionPlanRequestDto(_store, new BackupRetentionPolicyDto(0, 1, 0, 1, 0, false)));
        Assert.That(plan.Fingerprint, Is.Not.Empty);
        Assert.That(plan.Kept, Is.Not.Empty);
    }

    [Test]
    public async Task WithoutTheBootstrapToken_NothingAnswers()
    {
        using var anonymous = _factory.CreateClient();
        var response = await anonymous.PostAsJsonAsync("/api/backups/status", new BackupStatusRequestDto(_store, null));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That((await anonymous.PostAsJsonAsync("/api/backups/jobs/verify", new BackupVerifyRequestDto(_store, null, "quick"))).StatusCode,
            Is.EqualTo(HttpStatusCode.Unauthorized));
    }
}
