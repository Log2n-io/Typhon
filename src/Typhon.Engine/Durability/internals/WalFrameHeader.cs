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

    /// <summary>Sentinel value indicating end-of-buffer padding.</summary>
    public const int PaddingSentinel = -1;

    /// <summary>Expected size of this struct in bytes.</summary>
    public const int SizeInBytes = 8;
}
