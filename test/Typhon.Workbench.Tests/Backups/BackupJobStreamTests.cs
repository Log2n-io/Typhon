using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Workbench.Backups;
using Typhon.Workbench.Dtos.Backups;
using Typhon.Workbench.Tests.Fixtures;

namespace Typhon.Workbench.Tests.Backups;

/// <summary>
/// #1417 task 2: a job's SSE stream — the terminal frame reaches a client that subscribes after the job ended (subscribe-before-read), progress then the
/// terminal one during, 404 for an unknown job, and a burst of progress coalesced rather than queued.
/// </summary>
[TestFixture]
public sealed class BackupJobStreamTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private WorkbenchFactory _factory;
    private HttpClient _client;
    private BackupJobRegistry _jobs;
    private string _store;

    [SetUp]
    public void SetUp()
    {
        _factory = new WorkbenchFactory();
        _client = _factory.CreateAuthenticatedClient();
        _jobs = _factory.Services.GetRequiredService<BackupJobRegistry>();
        _store = Path.Combine(_factory.DemoDirectory, "store");
    }

    [TearDown]
    public void TearDown()
    {
        _jobs.CancelAll(TimeSpan.FromSeconds(5));
        _client.Dispose();
        _factory.Dispose();
    }

    private BackupJob Start(Action<BackupJobContext> body)
    {
        Assert.That(_jobs.TryStart("verify", _store, null, null, null, body, out var job), Is.True);
        return job;
    }

    private async Task<(HttpResponseMessage Response, StreamReader Reader)> Subscribe(Guid jobId, CancellationToken ct)
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/backups/jobs/{jobId}/stream"),
            HttpCompletionOption.ResponseHeadersRead, ct);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType!.MediaType, Is.EqualTo("text/event-stream"));
        return (response, new StreamReader(await response.Content.ReadAsStreamAsync(ct)));
    }

    private static async Task<List<(string Event, BackupJobDto Job)>> ReadToEnd(StreamReader reader, CancellationToken ct)
    {
        var frames = new List<(string, BackupJobDto)>();
        while (await SseFrameReader.ReadFrameAsync(reader, ct) is { } frame)
        {
            frames.Add((frame.EventType, JsonSerializer.Deserialize<BackupJobDto>(frame.Data, Json)));
        }

        return frames;
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task Subscribe_AfterTheJobEnded_ReceivesItsTerminalFrame()
    {
        var job = Start(ctx => ctx.SetResult(d => d with { Verify = new BackupVerifyReportDto("Quick", "Ok", [], [], [], 1, 0, 0) }));
        Assert.That(job.Completion.Wait(TimeSpan.FromSeconds(5)), Is.True);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var (response, reader) = await Subscribe(job.Id, cts.Token);
        using (response)
        using (reader)
        {
            var frames = await ReadToEnd(reader, cts.Token);
            Assert.That(frames.Select(f => f.Event), Is.EqualTo(new[] { "done" }), "one terminal frame, then the server closes");
            Assert.That(frames[0].Job.Verify.Verdict, Is.EqualTo("Ok"), "the frame carries the result");
        }
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task Subscribe_DuringTheJob_StreamsProgress_ThenItsTerminalFrame()
    {
        using var reported = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var job = Start(ctx =>
        {
            ctx.Report("packs", 1, 2, "packs");
            reported.Set();
            release.Wait(ctx.Token);
            throw new BackupJobException("store_damaged", "pack 2 is gone");
        });
        Assert.That(reported.Wait(TimeSpan.FromSeconds(5)), Is.True);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var (response, reader) = await Subscribe(job.Id, cts.Token);
        using (response)
        using (reader)
        {
            var first = await SseFrameReader.ReadFrameAsync(reader, cts.Token);
            Assert.That(first?.EventType, Is.EqualTo("progress"));
            Assert.That(JsonSerializer.Deserialize<BackupJobDto>(first!.Value.Data, Json).Completed, Is.EqualTo(1));

            release.Set();
            var rest = await ReadToEnd(reader, cts.Token);
            Assert.That(rest[^1].Event, Is.EqualTo("error"));
            Assert.That(rest[^1].Job.Error, Is.EqualTo(new BackupJobErrorDto("store_damaged", "pack 2 is gone")));
        }
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task ACancelledJob_EndsItsStream_WithACancelledFrame()
    {
        using var started = new ManualResetEventSlim();
        var job = Start(ctx =>
        {
            started.Set();
            ctx.Token.WaitHandle.WaitOne();
            ctx.Token.ThrowIfCancellationRequested();
        });
        Assert.That(started.Wait(TimeSpan.FromSeconds(5)), Is.True);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var (response, reader) = await Subscribe(job.Id, cts.Token);
        using (response)
        using (reader)
        {
            Assert.That(_jobs.Cancel(job.Id), Is.True);
            var frames = await ReadToEnd(reader, cts.Token);
            Assert.That(frames[^1].Event, Is.EqualTo("cancelled"));
        }
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task AnUnknownJob_Is404()
    {
        var response = await _client.GetAsync($"/api/backups/jobs/{Guid.NewGuid()}/stream");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    [CancelAfter(10_000)]
    public async Task ABurstOfProgress_IsCoalesced_NotQueued()
    {
        using var subscribed = new ManualResetEventSlim();
        using var burst = new ManualResetEventSlim();
        var job = Start(ctx =>
        {
            ctx.Report("restore", 0, 1000, "pages");
            subscribed.Wait(ctx.Token);
            for (var i = 1; i <= 1000; i++)
            {
                ctx.Report("restore", i, 1000, "pages");
            }

            burst.Set();
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var (response, reader) = await Subscribe(job.Id, cts.Token);
        using (response)
        using (reader)
        {
            subscribed.Set();
            var frames = await ReadToEnd(reader, cts.Token);
            Assert.That(frames[^1].Event, Is.EqualTo("done"));
            Assert.That(frames[^1].Job.Completed, Is.EqualTo(1000));
            Assert.That(frames.Count, Is.LessThanOrEqualTo(10), "1 000 updates in a burst reach the client as a handful of frames");
        }
    }
}
