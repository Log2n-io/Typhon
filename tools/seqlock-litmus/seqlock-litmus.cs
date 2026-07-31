#:property AllowUnsafeBlocks=true

// Seqlock litmus harness for #579 / rule SL-07.
//
// Reproduces PagedMMF's page seqlock in isolation so the memory-ordering bug can actually be OBSERVED rather than only reasoned about. Running the full engine
// will not do: the protocol is a few instructions buried under milliseconds of I/O, so the reorder window is a vanishing fraction of the loop. Reduced to a
// bare writer/reader pair it becomes most of the loop.
//
//   dotnet run tools/seqlock-litmus/seqlock-litmus.cs selftest    <- MUST tear. Proves the harness can see tearing at all.
//   dotnet run tools/seqlock-litmus/seqlock-litmus.cs unfenced    <- the pre-#579 protocol. Tears on a weak model; will not on x64.
//   dotnet run tools/seqlock-litmus/seqlock-litmus.cs fenced      <- the post-#579 protocol. Must never tear anywhere.
//   dotnet run tools/seqlock-litmus/seqlock-litmus.cs bench       <- unfenced vs fenced throughput: the cost of the barriers.
//
// Optional trailing args: <seconds> <readerThreads> <quietSpins>   (defaults: 10 4 400)
//
// quietSpins is the writer's pause between passes, and it matters more than it looks. With the writer looping flat out the counter is odd essentially all the
// time: readers skip billions of times and complete almost no copies, so the window this harness exists to probe is never entered. The tearing window needs the
// writer to START a pass while a reader is mid-memcpy, which requires the page to be quiescent often enough for readers to begin one. Tune until `validated`
// and `retries` are both large; if `skipped` dwarfs them by orders of magnitude, raise quietSpins.
//
// READ THIS BEFORE INTERPRETING A RESULT
//
//   A clean `unfenced` run is NOT evidence that the code was fine. Weak-memory reorderings are permitted, not mandatory — the window has to open AND the
//   reordering has to happen AND the tear has to land where it is checked. Apple Silicon in particular is far more conservative than the ARM spec allows.
//   `selftest` exists to keep that honest: if the harness cannot detect tearing when the protocol is removed entirely, a clean `unfenced` result means the
//   harness is broken, not that the protocol was sound.
//
//   `bench` is worth running on both machines, but read the two sides separately — they do NOT cost the same.
//
//     reader side: Volatile.Read is a plain mov on x64 and the conditional barrier folds away entirely, so the reader fix is genuinely free there. On arm64
//                  it becomes ldar plus a dmb ish per validated copy, and that is where a real cost can appear.
//     writer side: Interlocked.Increment is `lock inc` on x64 and ldaxr/stlxr on arm64 — NOT free on either. It is paid twice per page latch/unlatch pair,
//                  against a path that already takes two lock acquisitions, so it should disappear into the noise in the engine even though it shows up here.
//
//   The harness reports writer passes and reader copies as separate figures for exactly this reason. A single blended percentage would attribute the writer's
//   cost to the reader's barriers and vice versa.

using System.Diagnostics;
using System.Runtime.InteropServices;

var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "selftest";
var seconds = args.Length > 1 && int.TryParse(args[1], out var s) ? s : 10;
var readers = args.Length > 2 && int.TryParse(args[2], out var r) ? r : 4;
var quietSpins = args.Length > 3 && int.TryParse(args[3], out var q) ? q : 400;
Harness.QuietSpins = quietSpins;

Console.WriteLine($"seqlock litmus — {RuntimeInformation.ProcessArchitecture}, {Environment.ProcessorCount} logical cores, .NET {Environment.Version}");
Console.WriteLine($"mode={mode} duration={seconds}s readers={readers} quietSpins={quietSpins} page={Harness.PageBytes}B");
Console.WriteLine();

switch (mode)
{
    case "selftest":
        Report(Harness.Run(Harness.Mode.NoProtocol, seconds, readers), expectTearing: true);
        break;
    case "unfenced":
        Report(Harness.Run(Harness.Mode.Unfenced, seconds, readers), expectTearing: false);
        break;
    case "fenced":
        Report(Harness.Run(Harness.Mode.Fenced, seconds, readers), expectTearing: false);
        break;
    case "bench":
        var u = Harness.Run(Harness.Mode.Unfenced, seconds, readers);
        var f = Harness.Run(Harness.Mode.Fenced, seconds, readers);

        Console.WriteLine($"{"",-10} {"writer passes/s",18} {"reader copies/s",18}");
        Console.WriteLine($"{"unfenced",-10} {u.WriterPasses / (double)seconds,18:N0} {u.Validated / (double)seconds,18:N0}");
        Console.WriteLine($"{"fenced",-10} {f.WriterPasses / (double)seconds,18:N0} {f.Validated / (double)seconds,18:N0}");
        Console.WriteLine();

        var writerDelta = u.WriterPasses == 0 ? 0 : (u.WriterPasses - f.WriterPasses) * 100.0 / u.WriterPasses;
        var readerDelta = u.Validated == 0 ? 0 : (u.Validated - f.Validated) * 100.0 / u.Validated;
        Console.WriteLine($"writer cost (2x Interlocked.Increment per pass) : {writerDelta,6:F1}%");
        Console.WriteLine($"reader cost (Volatile.Read x2 + LoadLoad fence) : {readerDelta,6:F1}%");
        Console.WriteLine();
        Console.WriteLine("RUN THIS SEVERAL TIMES BEFORE BELIEVING EITHER NUMBER. Single-run variance is large: on a 32-core x64 box successive runs gave");
        Console.WriteLine("3.6%/16.8%, then 0.5%/-0.3%, then 0.2%/1.1%. A negative figure is the tell that you are reading noise. Only a delta that survives");
        Console.WriteLine("repetition is real — measured on x64, both settle at roughly zero, which is what theory predicts.");
        Console.WriteLine();
        Console.WriteLine("The two columns are also coupled: a slower writer changes the duty cycle and therefore how many copies readers complete, so the");
        Console.WriteLine("reader column is indicative rather than an isolated measurement. On x64 the reader's conditional barrier folds away entirely and");
        Console.WriteLine("Volatile.Read emits a plain mov (it still constrains the JIT, which is not free in a loop this tight, but is unmeasurable against");
        Console.WriteLine("the engine's per-page memcpy + CRC + write). On arm64 both columns can carry real cost — that is what this mode is for.");
        break;
    default:
        Console.WriteLine("unknown mode; use selftest | unfenced | fenced | bench");
        return 2;
}

return Harness.ExitCode;

void Report(Harness.Result res, bool expectTearing)
{
    Console.WriteLine($"validated copies : {res.Validated:N0}");
    Console.WriteLine($"retries (counter changed) : {res.Retries:N0}");
    Console.WriteLine($"skipped (counter odd)     : {res.Skipped:N0}");
    Console.WriteLine($"TORN COPIES ACCEPTED AS VALID : {res.Torn:N0}");
    Console.WriteLine();

    if (expectTearing)
    {
        if (res.Torn > 0)
        {
            Console.WriteLine("PASS — the harness detects tearing when the protocol is removed, so a clean 'unfenced' run below is meaningful.");
        }
        else
        {
            Console.WriteLine("HARNESS BROKEN — no tearing even with NO protocol at all. Every other result from this run is worthless.");
            Console.WriteLine("Raise the duration or the reader count; if it still cannot tear, the writer is not overlapping the readers.");
            Harness.ExitCode = 1;
        }

        return;
    }

    if (res.Torn > 0)
    {
        Console.WriteLine("TEARING OBSERVED — a copy passed seqlock validation while containing bytes from two different writer generations.");
        Console.WriteLine("In PagedMMF that copy is CRC-stamped over the torn bytes and written, so ADR-015 checksum validation passes on reload.");
        Harness.ExitCode = 1;
    }
    else
    {
        Console.WriteLine("no tearing observed in this run — which is NOT proof of correctness (see the header note). On x64 this outcome is expected");
        Console.WriteLine("for both modes, because TSO supplies the ordering the unfenced protocol omits.");
    }
}

static unsafe class Harness
{
    internal const int PageBytes = 4096;
    internal const int PageInts = PageBytes / sizeof(int);

    // Slot 0 stands in for PageBaseHeader.ModificationCounter — in the engine the counter lives inside the page the memcpy copies, and that co-location is
    // part of what the reader has to get right, so the harness keeps it.
    private const int CounterIndex = 0;
    private const int DataStart = 1;

    internal static int ExitCode;

    /// <summary>Writer pause between passes — see the header note; without it the page is never quiescent and readers never complete a copy.</summary>
    internal static int QuietSpins = 400;

    internal enum Mode
    {
        NoProtocol,   // self-test: no counter discipline at all — must tear
        Unfenced,     // pre-#579: plain ++counter, plain protocol loads
        Fenced,       // post-#579: Interlocked writer, Volatile reader + arch-conditional barrier
    }

    internal readonly record struct Result(long Validated, long Retries, long Skipped, long Torn)
    {
        /// <summary>Writer passes completed — reported separately because the writer and reader fixes have different costs on both ISAs.</summary>
        internal long WriterPasses { get; init; }
    }

    private static volatile bool _stop;
    private static long _writerPasses;

    internal static Result Run(Mode mode, int seconds, int readerCount)
    {
        _stop = false;
        _writerPasses = 0;

        var page = (int*)NativeMemory.AlignedAlloc(PageBytes, 4096);
        new Span<int>(page, PageInts).Clear();

        try
        {
            long validated = 0, retries = 0, skipped = 0, torn = 0;

            var writer = new Thread(() => Writer(page, mode)) { IsBackground = true, Name = "seqlock-writer" };
            var readerThreads = new Thread[readerCount];
            var results = new Result[readerCount];

            for (int i = 0; i < readerCount; i++)
            {
                int slot = i;
                readerThreads[slot] = new Thread(() => results[slot] = Reader(page, mode)) { IsBackground = true, Name = $"seqlock-reader-{slot}" };
            }

            writer.Start();
            foreach (var t in readerThreads)
            {
                t.Start();
            }

            Thread.Sleep(seconds * 1000);
            _stop = true;

            writer.Join();
            foreach (var t in readerThreads)
            {
                t.Join();
            }

            foreach (var res in results)
            {
                validated += res.Validated;
                retries += res.Retries;
                skipped += res.Skipped;
                torn += res.Torn;
            }

            return new Result(validated, retries, skipped, torn) { WriterPasses = _writerPasses };
        }
        finally
        {
            NativeMemory.AlignedFree(page);
        }
    }

    /// <summary>
    /// Mirrors TryLatchPageExclusive → caller writes → UnlatchPageExclusive. Each pass stamps the whole data area with a single generation number, so any
    /// copy containing two distinct values is definitionally torn.
    /// </summary>
    private static void Writer(int* page, Mode mode)
    {
        int generation = 1;

        while (!_stop)
        {
            // Open: even -> odd.
            if (mode == Mode.Fenced)
            {
                Interlocked.Increment(ref page[CounterIndex]);
            }
            else if (mode == Mode.Unfenced)
            {
                page[CounterIndex]++;
            }

            for (int i = DataStart; i < PageInts; i++)
            {
                page[i] = generation;
            }

            // Close: odd -> even. Release semantics, NOT a full fence — "prior stores visible before this store" is exactly what release gives, so the
            // engine uses Volatile.Write here while the open site above needs Interlocked. Mirrored deliberately: a harness that fenced both sides the same
            // way would not be testing the protocol that actually ships.
            if (mode == Mode.Fenced)
            {
                Volatile.Write(ref page[CounterIndex], page[CounterIndex] + 1);
            }
            else if (mode == Mode.Unfenced)
            {
                page[CounterIndex]++;
            }

            generation++;
            _writerPasses++;

            // Leave the page quiescent long enough for a reader to snapshot an even counter and start copying — the writer's next pass then lands mid-copy,
            // which is the window the whole harness exists to probe.
            if (QuietSpins > 0)
            {
                Thread.SpinWait(QuietSpins);
            }
        }
    }

    /// <summary>Mirrors CopyPageWithSeqlock, including copying the counter along with the page.</summary>
    private static Result Reader(int* page, Mode mode)
    {
        long validated = 0, retries = 0, skipped = 0, torn = 0;

        var copy = (int*)NativeMemory.AlignedAlloc(PageBytes, 4096);
        try
        {
            while (!_stop)
            {
                if (mode == Mode.NoProtocol)
                {
                    // No counter discipline whatsoever — copy straight through and accept it. This is what proves the tear detector works.
                    Buffer.MemoryCopy(page, copy, PageBytes, PageBytes);
                    validated++;
                    if (IsTorn(copy))
                    {
                        torn++;
                    }

                    continue;
                }

                int counter = mode == Mode.Fenced
                    ? Volatile.Read(ref page[CounterIndex])
                    : page[CounterIndex];

                if ((counter & 1) != 0)
                {
                    skipped++;
                    continue;
                }

                Buffer.MemoryCopy(page, copy, PageBytes, PageBytes);

                // The engine's arch-conditional LoadLoad barrier. Without it the memcpy's plain loads may sink below the validating re-read, and the
                // validation then checks a counter snapshot older than the data it is validating.
                if (mode == Mode.Fenced && !System.Runtime.Intrinsics.X86.X86Base.IsSupported)
                {
                    Interlocked.MemoryBarrier();
                }

                int after = mode == Mode.Fenced
                    ? Volatile.Read(ref page[CounterIndex])
                    : page[CounterIndex];

                if (after != counter)
                {
                    retries++;
                    continue;
                }

                validated++;
                if (IsTorn(copy))
                {
                    torn++;
                }
            }
        }
        finally
        {
            NativeMemory.AlignedFree(copy);
        }

        return new Result(validated, retries, skipped, torn);
    }

    /// <summary>A validated copy must contain exactly one writer generation across its whole data area.</summary>
    private static bool IsTorn(int* copy)
    {
        int first = copy[DataStart];
        for (int i = DataStart + 1; i < PageInts; i++)
        {
            if (copy[i] != first)
            {
                return true;
            }
        }

        return false;
    }
}
