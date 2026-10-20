using System.Threading.Channels;
using Typhon.Workbench.Backups;
using Typhon.Workbench.Dtos.Backups;

namespace Typhon.Workbench.Streams;

/// <summary>
/// SSE stream of one backup job (#1417 §3.5): typed <c>progress</c> frames, then one terminal <c>done</c> / <c>error</c> / <c>cancelled</c> frame, after
/// which the server closes the stream. Every payload is the whole <see cref="BackupJobDto"/>, so a frame never depends on an earlier one.
/// </summary>
/// <remarks>
/// <para>Not gated by the bootstrap token (<c>EventSource</c> cannot send headers): the job id — random, 128-bit, issued only by a token-gated POST — is the
/// capability, as a session id is for the session streams (WB-2).</para>
/// <para>Subscribes to the job <b>before</b> reading its state, so a job ending in between still delivers its terminal frame (the race
/// <see cref="ProfilerBuildProgressStream"/> closes the same way). Progress is coalesced to the newest snapshot by a bounded(1, DropOldest) channel on top
/// of the job's own throttle, so a burst never queues; a terminal snapshot is never dropped (it is the last write).</para>
/// </remarks>
public static class BackupJobStream
{
    private static readonly TimeSpan KeepaliveInterval = TimeSpan.FromSeconds(30);

    public const string ProgressEvent = "progress";

    public static async Task HandleAsync(Guid jobId, HttpContext ctx, BackupJobRegistry jobs, CancellationToken ct)
    {
        var job = jobs.Get(jobId);
        if (job == null)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await SseExtensions.WriteSseHeadersAsync(ctx, ct);

        var channel = Channel.CreateBounded<BackupJobDto>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        void Listener(BackupJobDto dto) => channel.Writer.TryWrite(dto);

        job.Changed += Listener;
        try
        {
            // After the subscribe: the current state, which may already be terminal. A terminal transition between that read and the write would be dropped
            // by it (DropOldest), so a job found terminal now writes its terminal state once more: the last write is always the newest state.
            channel.Writer.TryWrite(job.Snapshot());
            if (job.IsTerminal)
            {
                channel.Writer.TryWrite(job.Snapshot());
            }

            using var keepalive = new PeriodicTimer(KeepaliveInterval);
            var readTask = channel.Reader.ReadAsync(ct).AsTask();
            var keepaliveTask = keepalive.WaitForNextTickAsync(ct).AsTask();
            while (!ct.IsCancellationRequested)
            {
                var winner = await Task.WhenAny(readTask, keepaliveTask);
                if (winner == readTask)
                {
                    var dto = await readTask;
                    var terminal = BackupJobStates.IsTerminal(dto.State);
                    await SseExtensions.WriteEventAsync(ctx, terminal ? dto.State : ProgressEvent, dto, ct);
                    if (terminal)
                    {
                        return;
                    }

                    readTask = channel.Reader.ReadAsync(ct).AsTask();
                }
                else
                {
                    await keepaliveTask;
                    await SseExtensions.WriteCommentAsync(ctx, "keepalive", ct);
                    keepaliveTask = keepalive.WaitForNextTickAsync(ct).AsTask();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected.
        }
        catch (IOException)
        {
            // Kestrel can surface a forcible disconnect as IOException before the token fires.
        }
        finally
        {
            job.Changed -= Listener;
            channel.Writer.TryComplete();
        }
    }
}
