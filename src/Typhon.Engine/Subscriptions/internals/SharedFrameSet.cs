using System;
using System.Runtime.InteropServices;
using Typhon.Protocol;

namespace Typhon.Engine.Internals;

/// <summary>
/// One worker's cache of the last frame it encoded, so the sessions that would encode the same bytes do not each encode them.
/// </summary>
/// <remarks>
/// <para>
/// <b>The claim, and why it holds.</b> Two sessions on the same profile, at the same baseline, both fully synced and owing no deferred enter, produce
/// <b>identical frames by construction</b> — not by coincidence. Their interest reaches the same clusters because the profile is the same; their known-sets
/// hold the same entities because a complete view with nothing owed IS "everything the interest reached"; and the emit rule carries a group iff its tick
/// beats the baseline, which is the same number for both. Nothing about the encode depends on anything else that differs between them. This is what
/// [02 § 7](../../../claude/design/Subscriptions/02-execution.md) means by encode-once, and it is the whole of AC-1's argument at 110 sessions.
/// </para>
/// <para>
/// <b>What is shared is the ENCODE, not the block and not the walk.</b> Each session still gathers its own records and still commits them to its own
/// known-set: a shared frame tells N clients about the same enters and leaves, and every one of those N known-sets has to learn about them or the next
/// frame's emit rule is wrong. What is skipped is the codec work — the header, the per-archetype blocks, the gaps, the bit-packed state bodies — which is
/// the part that scales with fields and is the part 02 § 7 is about. Each session then gets its own pool block and a memcpy into it.
/// </para>
/// <para>
/// <b>Why not one refcounted block for all of them.</b> That would save the memcpy and the pool traffic, and it would make the frame's memory owned by N
/// send states at once — on a path where <c>SendPump</c>'s own contract is that the producer is the block's only owner and recycles it. Two owners under
/// SUB-04's release/acquire pairs is a much larger change than the copy it removes, and [09 § 7 R7](../../../claude/design/Subscriptions/09-phase1-build-plan.md)
/// already names "one encode, N copies" as the form the criterion takes. The refcounted variant stays available if a measurement ever shows the copy
/// mattering.
/// </para>
/// <para>
/// <b>Per worker, so a group that straddles chunks encodes once per chunk.</b> Sessions are partitioned across workers, so 110 identical sessions on eight
/// workers cost eight encodes rather than one. That is deliberate: the alternative is cross-worker coordination on the tick path to save seven encodes out
/// of 110, which is the wrong trade. The counter says which world a run is in.
/// </para>
/// <para>
/// <b>The key is a cross-check, not the guarantee.</b> The guarantee is the construction argument above. The key additionally carries the tick, the flags
/// and the per-archetype record counts, so a divergence that construction did not anticipate is very unlikely to reuse silently — and
/// <see cref="VerifyReuseForTest"/> turns "very unlikely" into "proven" for the fixture that has to prove it, by encoding both and comparing.
/// </para>
/// </remarks>
internal sealed unsafe class SharedFrameSet : IDisposable
{
    private byte* _bytes;
    private int _capacity;
    private int _length;
    private int[] _counts = [];
    private string _profile;
    private long _baseline = -1;
    private long _tick = -1;
    private TickFlags _flags;
    private bool _valid;

    /// <summary>How many frames this worker encoded for real.</summary>
    public long Encodes { get; private set; }

    /// <summary>How many frames this worker produced by copying an encode it had already done. AC-1's counter.</summary>
    public long Copies { get; private set; }

    /// <summary>
    /// Counts one real encode.
    /// </summary>
    /// <remarks>
    /// Called on every encode, shareable or not — deliberately separate from <see cref="Store"/>, which only runs for the shareable ones. Counting inside
    /// Store was the first shape and it was wrong in the direction that flatters the feature: a session that encoded its own frame because it could not
    /// share was invisible, so a tick with three encodes reported one and AC-1's ratio would have overstated the sharing. The test that caught it is
    /// <c>ASessionStillFillingItsViewGetsItsOwnFrame</c>.
    /// </remarks>
    public void NoteEncode() => Encodes++;

    /// <summary>Drops the cached encode. Called when a worker starts a tick, so nothing is ever reused across ticks.</summary>
    /// <remarks>
    /// The tick is part of the key as well, which makes this belt and braces — but the cheap reset is what keeps a stale pointer from outliving the buffer
    /// it points into if the key logic is ever changed.
    /// </remarks>
    public void BeginTick() => _valid = false;

    /// <summary>
    /// Whether the frame this session is about to encode is one this worker has already encoded.
    /// </summary>
    /// <param name="profile">The session's bound profile.</param>
    /// <param name="baseline">Its baseline tick.</param>
    /// <param name="tick">The tick being assembled.</param>
    /// <param name="flags">The frame's tick flags.</param>
    /// <param name="counts">The per-archetype, per-list record counts, in a fixed order.</param>
    /// <param name="bytes">The encoded frame, valid until the next <see cref="Store"/> on this worker.</param>
    /// <returns><see langword="true"/> when the caller may copy <paramref name="bytes"/> instead of encoding.</returns>
    public bool TryReuse(string profile, long baseline, long tick, TickFlags flags, ReadOnlySpan<int> counts, out ReadOnlySpan<byte> bytes)
    {
        bytes = default;
        if (!_valid || _tick != tick || _baseline != baseline || _flags != flags || !string.Equals(_profile, profile, StringComparison.Ordinal))
        {
            return false;
        }

        if (!counts.SequenceEqual(_counts.AsSpan(0, Math.Min(_counts.Length, counts.Length))) || counts.Length != _counts.Length)
        {
            return false;
        }

        bytes = new ReadOnlySpan<byte>(_bytes, _length);
        Copies++;
        return true;
    }

    /// <summary>Remembers an encode so the next identical session can copy it.</summary>
    /// <param name="profile">The session's bound profile.</param>
    /// <param name="baseline">Its baseline tick.</param>
    /// <param name="tick">The tick being assembled.</param>
    /// <param name="flags">The frame's tick flags.</param>
    /// <param name="counts">The per-archetype, per-list record counts.</param>
    /// <param name="bytes">The encoded frame.</param>
    public void Store(string profile, long baseline, long tick, TickFlags flags, ReadOnlySpan<int> counts, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > _capacity)
        {
            var capacity = _capacity == 0 ? 4096 : _capacity;
            while (capacity < bytes.Length)
            {
                capacity *= 2;
            }

            // Native, not a GC array: the span handed back by TryReuse is read while the tick runs, and a pointer into pool-free native memory is the
            // engine's own to hold. Nothing here ever addresses managed memory.
            _bytes = (byte*)NativeMemory.Realloc(_bytes, (nuint)capacity);
            _capacity = capacity;
        }

        bytes.CopyTo(new Span<byte>(_bytes, _capacity));
        _length = bytes.Length;

        if (_counts.Length != counts.Length)
        {
            _counts = new int[counts.Length];
        }

        counts.CopyTo(_counts);
        _profile = profile;
        _baseline = baseline;
        _tick = tick;
        _flags = flags;
        _valid = true;
    }

    /// <summary>The cached encode, for the fixture that re-encodes a shared session and requires the bytes to match.</summary>
    /// <returns>The bytes, or an empty span when nothing is cached.</returns>
    public ReadOnlySpan<byte> VerifyReuseForTest() => _valid ? new ReadOnlySpan<byte>(_bytes, _length) : default;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_bytes != null)
        {
            NativeMemory.Free(_bytes);
            _bytes = null;
        }

        _capacity = 0;
        _length = 0;
        _valid = false;
    }
}
