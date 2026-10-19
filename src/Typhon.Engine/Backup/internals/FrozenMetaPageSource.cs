using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// The meta pair (pages 0 and 1) as a capture froze it, as an <see cref="IPageSource"/>: <see cref="BootstrapReader.Read"/> over it selects the slot
/// exactly as an open of the restored image will, so the image's checkpoint LSN is read from the image itself (BKP-02).
/// </summary>
internal sealed class FrozenMetaPageSource : IPageSource
{
    private readonly byte[] _slot0;
    private readonly byte[] _slot1;

    /// <summary>Copies the two slot images.</summary>
    public FrozenMetaPageSource(ReadOnlySpan<byte> slot0, ReadOnlySpan<byte> slot1)
    {
        _slot0 = slot0.ToArray();
        _slot1 = slot1.ToArray();
    }

    /// <inheritdoc />
    public int PageCount => 2;

    /// <inheritdoc />
    public bool TryReadPage(int index, Span<byte> destination)
    {
        var source = index switch
        {
            0 => _slot0,
            1 => _slot1,
            _ => null,
        };
        if (source == null)
        {
            return false;
        }

        source.CopyTo(destination);
        return true;
    }

    /// <inheritdoc />
    public string Describe() => "frozen meta pair";

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
