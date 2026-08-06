using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Tests;

/// <summary>
/// The cooperative lock handoff (#621, design §2): a holder may advertise that it will release the database, and a
/// claimant that finds such a holder asks rather than failing.
///
/// <para><b>The one property that makes this safe to enable by default</b> is that the trigger is the <i>holder's</i>
/// advertisement, never the claimant's configuration. Two ordinary application instances therefore contend exactly as
/// they always did — the incumbent advertised nothing, so the claimant throws immediately. That is
/// <see cref="TwoOrdinaryEngines_StillCollideImmediately"/>, and it is the regression guard that matters most here:
/// everything else in this file adds behaviour, while that one asserts behaviour was <i>not</i> added where it should
/// not be.</para>
/// </summary>
[TestFixture]
[NonParallelizable]
internal sealed class LockHandoffTests
{
    private string _root;
    private string _dbDir;

    private static string DbName
    {
        get
        {
            const string prefix = "Handoff_";
            const int max = 63;
            var name = TestContext.CurrentContext.Test.Name.Replace('(', '_').Replace(')', '_');
            if (prefix.Length + name.Length > max)
            {
                name = name[^(max - prefix.Length)..];
            }
            return prefix + name;
        }
    }

    /// <summary>
    /// A process id that cannot exist. Deliberately not 0: on Windows that is the System Idle Process, which
    /// <c>GetProcessById</c> returns and whose <c>HasExited</c> throws ERROR_ACCESS_DENIED — so it exercises the
    /// "cannot inspect" path, not the "is dead" path these tests mean.
    /// </summary>
    private const int NeverAliveProcessId = int.MaxValue - 1;

    private string BundleDir => Path.Combine(_dbDir, $"{DbName}.typhon");
    private string LockPath => DatabaseLockFile.PathFor(BundleDir);
    private string RequestPath => DatabaseLockFile.RequestPathFor(BundleDir);

    [SetUp]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "Typhon.Tests", nameof(LockHandoffTests), DbName);
        _dbDir = Path.Combine(_root, "db");
        Directory.CreateDirectory(_dbDir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Builds a provider whose engine opens with the given lock posture. Separate providers rather than scopes of one,
    /// so holder and claimant carry genuinely different options — which is the whole subject here.
    /// </summary>
    private ServiceProvider BuildProvider(bool yieldable, TimeSpan? handoffTimeout = null)
    {
        var services = new ServiceCollection();
        services
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Error))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(opts =>
            {
                opts.DatabaseName = DbName;
                opts.DatabaseDirectory = _dbDir;
                opts.DatabaseCacheSize = (ulong)PagedMMF.MinimumCacheSize * 4;
                opts.YieldableLock = yieldable;
                // Short by default: these tests exercise the timeout path, and the 5 s production default would make the
                // suite wait for it.
                opts.LockHandoffTimeout = handoffTimeout ?? TimeSpan.FromMilliseconds(400);
            })
            .AddScopedDatabaseEngine();
        return services.BuildServiceProvider();
    }

    // ── AC15 · the advertisement ─────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void AnOrdinaryEngine_DoesNotAdvertiseYieldable()
    {
        using var provider = BuildProvider(yieldable: false);
        using var scope = provider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();

        Assert.That(DatabaseLockFile.TryReadLock(BundleDir, out var info), Is.True);
        Assert.That(info.Yieldable, Is.False, "handoff must be opt-in for the holder — a default-on advertisement would change how applications contend");
    }

    [Test]
    public void AnObserver_AdvertisesYieldable()
    {
        using var provider = BuildProvider(yieldable: true);
        using var scope = provider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();

        Assert.That(DatabaseLockFile.TryReadLock(BundleDir, out var info), Is.True);
        Assert.That(info.Yieldable, Is.True);
    }

    [Test]
    public void ALockFileWithoutTheField_ReadsAsNotYieldable()
    {
        // Every lock written before this protocol existed. Reading a missing field as "willing to yield" would make a
        // claimant wait on a holder that has never heard of the request file.
        Directory.CreateDirectory(BundleDir);
        File.WriteAllText(LockPath, $"{{\"pid\":{Environment.ProcessId},\"startedAt\":\"{DateTimeOffset.UtcNow:o}\",\"machineName\":\"{Environment.MachineName}\"}}");

        Assert.That(DatabaseLockFile.TryReadLock(BundleDir, out var info), Is.True);
        Assert.That(info.Yieldable, Is.False);
    }

    // ── AC19 · the behaviour that must NOT change ────────────────────────────────────────────────────────────────

    [Test]
    public void TwoOrdinaryEngines_StillCollideImmediately()
    {
        using var provider = BuildProvider(yieldable: false, handoffTimeout: TimeSpan.FromSeconds(30));
        using var holderScope = provider.CreateScope();
        using var holder = holderScope.ServiceProvider.GetRequiredService<DatabaseEngine>();

        var started = DateTime.UtcNow;
        Assert.Throws<DatabaseLockedException>(() =>
        {
            using var scope = provider.CreateScope();
            _ = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        });
        var elapsed = DateTime.UtcNow - started;

        Assert.Multiple(() =>
        {
            // The generous timeout above is the point: if a non-yieldable holder were ever waited on, this would take 30 s.
            Assert.That(elapsed, Is.LessThan(TimeSpan.FromSeconds(5)), "a non-yieldable holder must be refused immediately, never waited on");
            Assert.That(File.Exists(RequestPath), Is.False, "no claim may be published against a holder that never offered to yield");
        });
    }

    // ── AC16 · the claimant ──────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void AgainstAYieldableHolderThatNeverReleases_ItWaitsThenFails_NamingTheBrokenPromise()
    {
        using var holderProvider = BuildProvider(yieldable: true);
        using var holderScope = holderProvider.CreateScope();
        using var holder = holderScope.ServiceProvider.GetRequiredService<DatabaseEngine>();

        using var claimantProvider = BuildProvider(yieldable: false, handoffTimeout: TimeSpan.FromMilliseconds(400));

        var started = DateTime.UtcNow;
        var ex = Assert.Throws<DatabaseLockedException>(() =>
        {
            using var scope = claimantProvider.CreateScope();
            _ = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        });
        var elapsed = DateTime.UtcNow - started;

        Assert.Multiple(() =>
        {
            Assert.That(elapsed, Is.GreaterThan(TimeSpan.FromMilliseconds(300)), "it must actually have waited for the advertised release");
            // "Locked" alone would send the user hunting for a process that believes it cooperated.
            Assert.That(ex.InnerException, Is.TypeOf<TimeoutException>(), "a broken promise is a different diagnosis from a plain collision, and must read as one");
            Assert.That(ex.InnerException!.Message, Does.Contain("advertised"));
        });

        Assert.That(File.Exists(RequestPath), Is.False, "a claimant that gives up must retire its own claim — otherwise it pins the holder out of its database");
    }

    [Test]
    public void WhenTheHolderReleases_TheClaimantAcquiresAndRetiresItsOwnClaim()
    {
        var holderProvider = BuildProvider(yieldable: true);
        var holderScope = holderProvider.CreateScope();
        var holder = holderScope.ServiceProvider.GetRequiredService<DatabaseEngine>();

        // Stands in for the Workbench's coordinator: watch for a claim, then let go. Kept in-process so the test is
        // deterministic — the protocol is entirely file-mediated, so a same-process holder takes the identical path.
        var released = new ManualResetEventSlim();
        var yielder = Task.Run(() =>
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && !File.Exists(RequestPath))
            {
                Thread.Sleep(10);
            }
            holder.Dispose();
            holderScope.Dispose();
            holderProvider.Dispose();
            released.Set();
        });

        using var claimantProvider = BuildProvider(yieldable: false, handoffTimeout: TimeSpan.FromSeconds(5));
        using var claimantScope = claimantProvider.CreateScope();
        var claimant = claimantScope.ServiceProvider.GetRequiredService<DatabaseEngine>();

        Assert.That(claimant, Is.Not.Null, "the claimant must acquire once the holder steps aside");
        Assert.That(released.Wait(TimeSpan.FromSeconds(5)), Is.True);
        yielder.Wait(TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            // The claimant — not the holder — retires the request, and only after acquiring. Its presence is what tells a
            // holder not to re-take the database in the window between its release and the claimant's acquisition.
            Assert.That(File.Exists(RequestPath), Is.False, "the claimant must delete its own claim once it holds the lock");
            Assert.That(DatabaseLockFile.TryReadLock(BundleDir, out var info), Is.True);
            Assert.That(info.Yieldable, Is.False, "the claimant is an ordinary engine — it must not inherit the observer's advertisement");
            Assert.That(info.Pid, Is.EqualTo(Environment.ProcessId));
        });
    }

    [Test]
    public void HandoffTimeoutOfZero_OptsOutAndFailsFastEvenAgainstAnObserver()
    {
        using var holderProvider = BuildProvider(yieldable: true);
        using var holderScope = holderProvider.CreateScope();
        using var holder = holderScope.ServiceProvider.GetRequiredService<DatabaseEngine>();

        using var claimantProvider = BuildProvider(yieldable: false, handoffTimeout: TimeSpan.Zero);
        var ex = Assert.Throws<DatabaseLockedException>(() =>
        {
            using var scope = claimantProvider.CreateScope();
            _ = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        });

        Assert.That(ex.InnerException, Is.Null, "opting out is a plain collision, not a broken promise");
        Assert.That(File.Exists(RequestPath), Is.False, "opting out must not ask the holder for anything");
    }

    // ── AC18 · the failure modes from design §4 ──────────────────────────────────────────────────────────────────

    [Test]
    public void AClaimFromADeadProcess_IsRetired()
    {
        // "Claimant dies after writing the request." Left in place it would pin the holder out of its own database for
        // as long as the Workbench lived.
        Directory.CreateDirectory(BundleDir);
        File.WriteAllText(RequestPath, $"{{\"pid\":{NeverAliveProcessId},\"machineName\":\"{Environment.MachineName}\",\"requestedAt\":\"{DateTimeOffset.UtcNow:o}\"}}");

        Assert.That(DatabaseLockFile.HasLiveRequest(BundleDir, DateTimeOffset.UtcNow), Is.False);
        Assert.That(File.Exists(RequestPath), Is.False, "an orphaned claim must be removed, not merely ignored — nobody else will ever clean it up");
    }

    [Test]
    public void AClaimPastItsTimeToLive_IsRetired()
    {
        // "User cancels the app launch after the request." The pid may well still be alive — it is the claim that is stale.
        Directory.CreateDirectory(BundleDir);
        var stale = DateTimeOffset.UtcNow - DatabaseLockFile.RequestTimeToLive - TimeSpan.FromSeconds(1);
        File.WriteAllText(RequestPath, $"{{\"pid\":{Environment.ProcessId},\"machineName\":\"{Environment.MachineName}\",\"requestedAt\":\"{stale:o}\"}}");

        Assert.That(DatabaseLockFile.HasLiveRequest(BundleDir, DateTimeOffset.UtcNow), Is.False);
        Assert.That(File.Exists(RequestPath), Is.False);
    }

    [Test]
    public void AnUnreadableClaim_IsHonouredAnyway()
    {
        // "Partially-written request file → treat as a request. FAIL TOWARD YIELDING." Ignoring a half-written claim is
        // the one outcome the protocol must never produce: the claimant is waiting on a release that would never come.
        Directory.CreateDirectory(BundleDir);
        File.WriteAllText(RequestPath, "{\"pid\": 12");

        Assert.That(DatabaseLockFile.HasLiveRequest(BundleDir, DateTimeOffset.UtcNow), Is.True);
        Assert.That(File.Exists(RequestPath), Is.True, "an unreadable claim must not be retired — it cannot be shown to be abandoned");
    }

    [Test]
    public void ALiveClaim_IsReportedAndLeftAlone()
    {
        Directory.CreateDirectory(BundleDir);
        DatabaseLockFile.WriteRequest(BundleDir);

        Assert.That(DatabaseLockFile.HasLiveRequest(BundleDir, DateTimeOffset.UtcNow), Is.True);
        Assert.That(File.Exists(RequestPath), Is.True, "only the claimant retires a live claim");
    }

    [Test]
    public void ACrashedHolder_IsStillTakenOver_WithNoHandoffWait()
    {
        // "App crashes while holding" — a stale lock, which the existing dead-PID detection clears. It must not be routed
        // into the handoff wait just because the crashed process happened to advertise yieldable.
        Directory.CreateDirectory(BundleDir);
        File.WriteAllText(LockPath, DatabaseLockFile.SerializeLock(NeverAliveProcessId, DateTimeOffset.UtcNow, Environment.MachineName, yieldable: true));

        using var provider = BuildProvider(yieldable: false, handoffTimeout: TimeSpan.FromSeconds(30));
        var started = DateTime.UtcNow;
        using (var scope = provider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            Assert.That(DatabaseLockFile.TryReadLock(BundleDir, out var info), Is.True);
            Assert.That(info.Pid, Is.EqualTo(Environment.ProcessId), "the stale lock must have been replaced");
        }

        Assert.That(DateTime.UtcNow - started, Is.LessThan(TimeSpan.FromSeconds(5)), "a dead holder must be cleared, not waited on");
    }
}
