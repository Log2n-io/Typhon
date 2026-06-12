using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// 8-byte in-band frame header prepended to each claimed region inside the WAL commit buffer. The consumer walks frame-by-frame using these headers
/// to find published data.
/// </summary>
/// <remarks>
/// <para>
/// Publication protocol: The producer writes all record data first, then sets <see cref="FrameLength"/> via
/// <see cref="System.Threading.Interlocked.Exchange(ref int, int)"/> (store-release semantics). The consumer reads <see cref="FrameLength"/>; a non-zero
/// positive value means the data is safe to read.
/// </para>
/// <para>
/// Sentinel values for <see cref="FrameLength"/>:
/// <list type="bullet">
///   <item><description><c>0</c> — Not yet published (producer still writing)</description></item>
///   <item><description><c>&gt;0</c> — Published frame (total bytes including this header)</description></item>
///   <item><description><c>-1</c> — Padding sentinel marking end-of-buffer</description></item>
/// </list>
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct WalFrameHeader
{
    /// <summary>
    /// Total frame size including this header, or a sentinel value. Written atomically by the producer via Interlocked.Exchange after all record
    /// data is committed.
    /// </summary>
    public int FrameLength;

    /// <summary>
    /// Number of WAL records contained in this frame. Zero for padding frames or abandoned claims.
    /// </summary>
    public int RecordCount;

    /// <summary>
    /// Highest LSN contained in this frame (<c>FirstLSN + RecordCount - 1</c> from the producer's claim). Written by the producer in <see cref="WalCommitBuffer.Publish"/>
    /// before the <see cref="FrameLength"/> release store, so the single consumer sees it once the frame is published. The consumer takes the max of this field over the
    /// frames it actually drains to compute an honest durable watermark (LOG-05): <see cref="WalWriter.DurableLsn"/> never advances past an LSN whose frame has not been
    /// physically written. Replaces the prior <c>NextLsn - 1</c> peek, which over-reported by counting claims that were assigned an LSN but not yet drained (TXW-2). Zero
    /// for padding frames and abandoned claims (RecordCount == 0).
    /// </summary>
    public long LastLsn;

    /// <summary>Sentinel value indicating end-of-buffer padding.</summary>
    public const int PaddingSentinel = -1;

    /// <summary>Expected size of this struct in bytes.</summary>
    public const int SizeInBytes = 16;
}
