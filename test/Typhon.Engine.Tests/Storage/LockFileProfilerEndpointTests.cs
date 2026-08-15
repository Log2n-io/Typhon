using NUnit.Framework;
using System;
using System.IO;
using System.Text.Json;

namespace Typhon.Engine.Tests;

/// <summary>
/// The lock file advertises where the holder's live profiler can be reached, so an observer that has the database can
/// discover where to watch it without the user retyping an endpoint they never chose.
/// </summary>
/// <remarks>
/// The property under test is <b>compatibility discipline</b>, not the field itself. <c>yieldable</c> established the
/// rule this follows: <i>absent means the safe answer</i>, so a lock written by an older build, or by an engine running
/// without a profiler, can never be read as offering something it does not have. A regression here would make the
/// Workbench offer a "watch live" action that connects to nothing.
/// </remarks>
[TestFixture]
internal sealed class LockFileProfilerEndpointTests
{
    private string _dir;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "typhon-lockfile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    private void WriteLock(string json) => File.WriteAllText(DatabaseLockFile.PathFor(_dir), json);

    [Test]
    public void EndpointRoundTrips()
    {
        WriteLock(DatabaseLockFile.SerializeLock(1234, DateTimeOffset.UtcNow, "HOST", yieldable: false, profilerEndpoint: "localhost:9100"));

        Assert.That(DatabaseLockFile.TryReadLock(_dir, out var info), Is.True);
        Assert.That(info.ProfilerEndpoint, Is.EqualTo("localhost:9100"));
    }

    [Test]
    public void NoProfiler_SerialisesToAPreExistingLockFileShape()
    {
        // Byte-identical to what the previous build wrote. An engine with no live profiler must not start emitting a
        // new field — a null in the file is one more thing every reader has to have an opinion about.
        var withoutEndpoint = DatabaseLockFile.SerializeLock(7, DateTimeOffset.UnixEpoch, "HOST", yieldable: true);

        Assert.That(withoutEndpoint, Does.Not.Contain("profilerEndpoint"));
        using var doc = JsonDocument.Parse(withoutEndpoint);
        Assert.That(doc.RootElement.TryGetProperty("profilerEndpoint", out _), Is.False);
    }

    [Test]
    public void LockFromAnOlderBuild_ReadsAsNoProfiler()
    {
        // Exactly the shape shipped before this field existed.
        WriteLock("""{"pid":42,"startedAt":"2026-01-01T00:00:00.0000000+00:00","machineName":"HOST","yieldable":true}""");

        Assert.That(DatabaseLockFile.TryReadLock(_dir, out var info), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(info.Pid, Is.EqualTo(42), "the pre-existing fields must still parse");
            Assert.That(info.Yieldable, Is.True);
            Assert.That(info.ProfilerEndpoint, Is.Null, "absent must mean no profiler, never a fabricated endpoint");
        });
    }

    [Test]
    public void BlankOrNonStringEndpoint_ReadsAsNoProfiler()
    {
        // A half-written or malformed value must degrade to "no profiler" rather than to an endpoint that cannot connect:
        // the Workbench would otherwise offer an action that always fails.
        foreach (var raw in new[] { "\"\"", "\"   \"", "null", "1234", "{}" })
        {
            WriteLock($$"""{"pid":9,"startedAt":"2026-01-01T00:00:00.0000000+00:00","machineName":"HOST","yieldable":false,"profilerEndpoint":{{raw}}}""");
            Assert.That(DatabaseLockFile.TryReadLock(_dir, out var info), Is.True, $"lock with endpoint {raw} must still parse");
            Assert.That(info.ProfilerEndpoint, Is.Null, $"endpoint {raw} must read as absent");
        }
    }

    [Test]
    public void EndpointDoesNotDisturbTheYieldableDefault()
    {
        // The two optional fields must stay independent: advertising a profiler says nothing about willingness to yield.
        WriteLock("""{"pid":5,"startedAt":"2026-01-01T00:00:00.0000000+00:00","machineName":"HOST","profilerEndpoint":"localhost:9100"}""");

        Assert.That(DatabaseLockFile.TryReadLock(_dir, out var info), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(info.Yieldable, Is.False, "absent yieldable must still default false");
            Assert.That(info.ProfilerEndpoint, Is.EqualTo("localhost:9100"));
        });
    }
}
