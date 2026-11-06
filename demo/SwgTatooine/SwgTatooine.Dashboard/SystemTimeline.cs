using System;
using System.Buffers.Binary;
using System.Text;
using System.Threading;

namespace SwgTatooine.Dashboard;

/// <summary>
/// Ten minutes of the game server's per-system tick costs, for the "Server health" timeline (claude/scratch/swg-dashboard-system-timeline.md): every tick
/// <c>GET /typhon/systems/ticks</c> answered, and per second the mean per tick — what the page's main chart draws, one bar a second, and its drill-down, one
/// bar a tick.
/// </summary>
/// <remarks>
/// <para>
/// <b>Allocated once</b>: the rings are sized from the system count and the tick rate the first time both are known, and again only when either changes (a
/// game server restarted on another build). A poll parses into them in place with <see cref="BinaryPrimitives"/>; nothing per tick or per poll.
/// </para>
/// <para>
/// <b>Seconds are tick numbers ÷ ticks per second</b>, not wall clock: a second's ticks stay contiguous, so its drill-down is a slice. Under modulation
/// (multiplier &gt; 1) a "second" covers more wall time; the page says so.
/// </para>
/// <para>
/// <b>One writer</b> (the feed's poll) and readers on the page's circuits: the writer takes the lock for a poll's ingest, a reader for a copy-out, both a
/// few microseconds. A second's <see cref="SecondVersion"/> is what a circuit compares with what it last sent.
/// </para>
/// </remarks>
public sealed class SystemTimeline
{
    /// <summary>Seconds kept: ten minutes.</summary>
    public const int Seconds = 600;

    /// <summary>
    /// Bytes of one second in <see cref="CopySeconds"/>: f64 second · f64 firstTick · f32 meanWall · f32 maxWall · u16 ticks · u16 pad, then per system
    /// f32 meanCpu · u8 meanEffX10 · u8 maxTouched.
    /// </summary>
    public static int SecondBytes(int systems) => 28 + (systems * 6);

    /// <summary>
    /// Bytes of one tick in <see cref="CopyTicks"/>: f64 tick · f32 wallMs · u32 pad, then per system f32 cpu · f32 wall · u8 effX10 · u8 touched.
    /// </summary>
    public static int TickBytes(int systems) => 16 + (systems * 10);

    private readonly object _lock = new();

    private int _systems;
    private int _ticksPerSecond;
    private int _tickCapacity;
    private string[] _names = [];
    private int _workers;

    // Per tick, by tick number modulo the capacity.
    private long[] _tickNumber = [];
    private float[] _tickWall = [];
    private float[] _cpu = [];
    private float[] _wall = [];
    private byte[] _eff = [];
    private byte[] _touched = [];

    // Per second, by second modulo Seconds: sums over its ticks, divided on the way out.
    private long[] _secondId = [];
    private long[] _secondFirst = [];
    private long[] _secondVersion = [];
    private int[] _secondTicks = [];
    private float[] _secondWallSum = [];
    private float[] _secondWallMax = [];
    private float[] _secondCpuSum = [];
    private float[] _secondEffSum = [];
    private byte[] _secondTouchedMax = [];

    private long _lastTick = -1;
    private long _version;

    /// <summary>The systems' names, by index, as the server last named them.</summary>
    public string[] Names
    {
        get
        {
            lock (_lock)
            {
                return _names;
            }
        }
    }

    /// <summary>Systems per tick.</summary>
    public int SystemCount => Volatile.Read(ref _systems);

    /// <summary>The scheduler's workers, as the server last said.</summary>
    public int Workers => Volatile.Read(ref _workers);

    /// <summary>Ticks per second the seconds are bucketed by.</summary>
    public int TicksPerSecond => Volatile.Read(ref _ticksPerSecond);

    /// <summary>The newest tick held; −1 before the first. What the next poll's <c>after</c> is.</summary>
    public long LastTick => Interlocked.Read(ref _lastTick);

    /// <summary>Bumped by every ingest that changed something: a circuit with nothing newer has nothing to send.</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>Whether the next poll should ask for the names: none known yet.</summary>
    public bool NeedsNames => Volatile.Read(ref _systems) == 0;

    /// <summary>Forgets everything: the next poll starts from the server's oldest tick and asks for the names.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _systems = 0;
            _names = [];
            _lastTick = -1;
            if (_secondId.Length > 0)
            {
                Array.Fill(_secondId, -1L);
            }

            if (_tickNumber.Length > 0)
            {
                Array.Fill(_tickNumber, -1L);
            }

            _version++;
        }
    }

    /// <summary>
    /// One answer of <c>GET /typhon/systems/ticks</c>, parsed into the rings. A tick number behind the newest held (the game server restarted) clears them
    /// first; an answer that names a different system count re-sizes them. Returns false for a body that is not the feed's.
    /// </summary>
    /// <param name="body">The answer.</param>
    /// <param name="ticksPerSecond">From the server's tick target (<c>tickMs.target</c>): 1000 ÷ it.</param>
    /// <param name="hasNames">Whether the poll asked for the names block (<see cref="NeedsNames"/> when it was sent).</param>
    public bool Ingest(ReadOnlySpan<byte> body, int ticksPerSecond, bool hasNames)
    {
        if (body.Length < 28 || BinaryPrimitives.ReadUInt32LittleEndian(body) != 0x54595354 || BinaryPrimitives.ReadUInt16LittleEndian(body[4..]) != 1)
        {
            return false;
        }

        var systems = BinaryPrimitives.ReadUInt16LittleEndian(body[6..]);
        var first = BinaryPrimitives.ReadInt64LittleEndian(body[12..]);
        var count = BinaryPrimitives.ReadInt32LittleEndian(body[20..]);
        var workers = BinaryPrimitives.ReadInt32LittleEndian(body[24..]);
        var at = 28;
        ticksPerSecond = Math.Max(1, ticksPerSecond);

        lock (_lock)
        {
            string[] names = null;
            if (_systems == 0 && !hasNames)
            {
                // Cleared between the poll's question and its answer: the next poll asks for the names.
                return true;
            }

            if (hasNames)
            {
                // Asked for at the start (NeedsNames): the block follows the header.
                names = new string[systems];
                for (var i = 0; i < systems; i++)
                {
                    if (at + 2 > body.Length)
                    {
                        return false;
                    }

                    var length = BinaryPrimitives.ReadUInt16LittleEndian(body[at..]);
                    names[i] = Encoding.UTF8.GetString(body.Slice(at + 2, length));
                    at += 2 + length;
                }
            }
            if (names == null && systems != _systems)
            {
                // Another build behind the same address: start again, names first.
                _systems = 0;
                _lastTick = -1;
                _version++;
                return true;
            }

            if (names != null)
            {
                Size(systems, ticksPerSecond);
                _names = names;
            }
            else if (ticksPerSecond != _ticksPerSecond)
            {
                Size(systems, ticksPerSecond);
            }

            // Tick numbers behind the newest held: the server restarted (the feed also clears on its stats' tick, for a restart that answers nothing).
            if (count > 0 && first <= _lastTick)
            {
                ResetRings();
            }

            _workers = workers;
            var stride = 12 + (systems * 10);
            for (var t = 0; t < count && at + stride <= body.Length; t++, at += stride)
            {
                Add(body.Slice(at, stride));
            }

            _version++;
        }

        return true;
    }

    // One tick: into its slot of the tick ring, and into its second's sums.
    private void Add(ReadOnlySpan<byte> tick)
    {
        var number = BinaryPrimitives.ReadInt64LittleEndian(tick);
        var wallMs = BinaryPrimitives.ReadSingleLittleEndian(tick[8..]);
        if (number <= _lastTick)
        {
            return;
        }

        var slot = (int)(number % _tickCapacity);
        _tickNumber[slot] = number;
        _tickWall[slot] = wallMs;
        var second = number / _ticksPerSecond;
        var s = (int)(second % Seconds);
        if (_secondId[s] != second)
        {
            _secondId[s] = second;
            _secondFirst[s] = number;
            _secondTicks[s] = 0;
            _secondWallSum[s] = 0f;
            _secondWallMax[s] = 0f;
            _secondCpuSum.AsSpan(s * _systems, _systems).Clear();
            _secondEffSum.AsSpan(s * _systems, _systems).Clear();
            _secondTouchedMax.AsSpan(s * _systems, _systems).Clear();
        }

        _secondFirst[s] = Math.Min(_secondFirst[s], number);
        _secondTicks[s]++;
        _secondWallSum[s] += wallMs;
        _secondWallMax[s] = Math.Max(_secondWallMax[s], wallMs);
        var bySystem = tick[12..];
        var tickBase = slot * _systems;
        var secondBase = s * _systems;
        for (var i = 0; i < _systems; i++)
        {
            var row = bySystem.Slice(i * 10, 10);
            var cpu = BinaryPrimitives.ReadSingleLittleEndian(row);
            var eff = row[8];
            var touched = row[9];
            _cpu[tickBase + i] = cpu;
            _wall[tickBase + i] = BinaryPrimitives.ReadSingleLittleEndian(row[4..]);
            _eff[tickBase + i] = eff;
            _touched[tickBase + i] = touched;
            _secondCpuSum[secondBase + i] += cpu;
            _secondEffSum[secondBase + i] += eff;
            _secondTouchedMax[secondBase + i] = Math.Max(_secondTouchedMax[secondBase + i], touched);
        }

        _secondVersion[s] = _version + 1;
        _lastTick = number;
    }

    // The rings for this many systems and this tick rate: allocated here only, at the first answer and when either changes.
    private void Size(int systems, int ticksPerSecond)
    {
        _systems = systems;
        _ticksPerSecond = ticksPerSecond;
        _tickCapacity = Seconds * ticksPerSecond;
        _tickNumber = new long[_tickCapacity];
        _tickWall = new float[_tickCapacity];
        _cpu = new float[_tickCapacity * systems];
        _wall = new float[_tickCapacity * systems];
        _eff = new byte[_tickCapacity * systems];
        _touched = new byte[_tickCapacity * systems];
        _secondId = new long[Seconds];
        _secondFirst = new long[Seconds];
        _secondVersion = new long[Seconds];
        _secondTicks = new int[Seconds];
        _secondWallSum = new float[Seconds];
        _secondWallMax = new float[Seconds];
        _secondCpuSum = new float[Seconds * systems];
        _secondEffSum = new float[Seconds * systems];
        _secondTouchedMax = new byte[Seconds * systems];
        ResetRings();
    }

    private void ResetRings()
    {
        Array.Fill(_tickNumber, -1L);
        Array.Fill(_secondId, -1L);
        _lastTick = -1;
    }

    /// <summary>
    /// The seconds changed since <paramref name="sinceVersion"/>, oldest first, in <see cref="SecondBytes"/> records: what a circuit sends its canvas.
    /// 0 for <paramref name="sinceVersion"/> sends every second held.
    /// </summary>
    /// <param name="sinceVersion">The <see cref="Version"/> the circuit last sent.</param>
    /// <param name="version">The version this copy is as of.</param>
    /// <returns>The records, or an empty array.</returns>
    public byte[] CopySeconds(long sinceVersion, out long version)
    {
        lock (_lock)
        {
            version = _version;
            if (_systems == 0)
            {
                return [];
            }

            var n = 0;
            for (var s = 0; s < Seconds; s++)
            {
                n += _secondId[s] >= 0 && _secondVersion[s] > sinceVersion ? 1 : 0;
            }

            var size = SecondBytes(_systems);
            var bytes = new byte[n * size];
            var at = 0;
            var newest = _lastTick / _ticksPerSecond;
            for (var k = Seconds - 1; k >= 0; k--)
            {
                var second = newest - k;
                if (second < 0)
                {
                    continue;
                }

                var s = (int)(second % Seconds);
                if (_secondId[s] != second || _secondVersion[s] <= sinceVersion)
                {
                    continue;
                }

                WriteSecond(bytes.AsSpan(at, size), s);
                at += size;
            }

            return at == bytes.Length ? bytes : bytes.AsSpan(0, at).ToArray();
        }
    }

    private void WriteSecond(Span<byte> into, int s)
    {
        var ticks = Math.Max(1, _secondTicks[s]);
        BinaryPrimitives.WriteDoubleLittleEndian(into, _secondId[s]);
        BinaryPrimitives.WriteDoubleLittleEndian(into[8..], _secondFirst[s]);
        BinaryPrimitives.WriteSingleLittleEndian(into[16..], _secondWallSum[s] / ticks);
        BinaryPrimitives.WriteSingleLittleEndian(into[20..], _secondWallMax[s]);
        BinaryPrimitives.WriteUInt16LittleEndian(into[24..], (ushort)Math.Min(_secondTicks[s], ushort.MaxValue));
        BinaryPrimitives.WriteUInt16LittleEndian(into[26..], 0);
        var b = s * _systems;
        var row = into[28..];
        for (var i = 0; i < _systems; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(row[(i * 6)..], _secondCpuSum[b + i] / ticks);
            row[(i * 6) + 4] = (byte)Math.Min(255f, MathF.Round(_secondEffSum[b + i] / ticks));
            row[(i * 6) + 5] = _secondTouchedMax[b + i];
        }
    }

    /// <summary>The mean CPU per tick of each system over one second, and its tick count: what <see cref="CopySeconds"/> sends, for a test to hold.</summary>
    public bool TrySecond(long second, Span<float> meanCpu, out int ticks)
    {
        lock (_lock)
        {
            ticks = 0;
            if (_systems == 0 || second < 0)
            {
                return false;
            }

            var s = (int)(second % Seconds);
            if (_secondId[s] != second)
            {
                return false;
            }

            ticks = _secondTicks[s];
            for (var i = 0; i < _systems && i < meanCpu.Length; i++)
            {
                meanCpu[i] = _secondCpuSum[(s * _systems) + i] / Math.Max(1, ticks);
            }

            return true;
        }
    }

    /// <summary>One second's ticks, oldest first, in <see cref="TickBytes"/> records: the drill-down's bars. Empty for a second no longer held.</summary>
    public byte[] CopyTicks(long second)
    {
        lock (_lock)
        {
            if (_systems == 0 || second < 0)
            {
                return [];
            }

            var s = (int)(second % Seconds);
            if (_secondId[s] != second)
            {
                return [];
            }

            var size = TickBytes(_systems);
            var first = second * _ticksPerSecond;
            var bytes = new byte[_ticksPerSecond * size];
            var at = 0;
            for (var number = first; number < first + _ticksPerSecond; number++)
            {
                var slot = (int)(number % _tickCapacity);
                if (_tickNumber[slot] != number)
                {
                    continue;
                }

                var into = bytes.AsSpan(at, size);
                BinaryPrimitives.WriteDoubleLittleEndian(into, number);
                BinaryPrimitives.WriteSingleLittleEndian(into[8..], _tickWall[slot]);
                var b = slot * _systems;
                for (var i = 0; i < _systems; i++)
                {
                    var row = into.Slice(16 + (i * 10), 10);
                    BinaryPrimitives.WriteSingleLittleEndian(row, _cpu[b + i]);
                    BinaryPrimitives.WriteSingleLittleEndian(row[4..], _wall[b + i]);
                    row[8] = _eff[b + i];
                    row[9] = _touched[b + i];
                }

                at += size;
            }

            return at == bytes.Length ? bytes : bytes.AsSpan(0, at).ToArray();
        }
    }
}
