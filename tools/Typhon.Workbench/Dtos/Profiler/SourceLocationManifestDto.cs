namespace Typhon.Workbench.Sessions;

/// <summary>
/// Compile-time source-location manifest received from the engine in the live-attach init handshake
/// (issue #293, Phase 3). Same wire shape as the trailing sections in a <c>.typhon-trace</c> file —
/// the client uses both arrays together to resolve a span's <c>siteId</c> to a file:line:method.
/// </summary>
public sealed record SourceLocationManifestDto(
    SourceLocationFileDto[] Files,
    SourceLocationEntryDto[] Entries)
{
    public static SourceLocationManifestDto Empty { get; } = new(System.Array.Empty<SourceLocationFileDto>(), System.Array.Empty<SourceLocationEntryDto>());
}

/// <summary>One entry in the FileTable: a fileId-indexed source path.</summary>
public sealed record SourceLocationFileDto(ushort FileId, string Path);

/// <summary>One entry in the SourceLocationManifest: a site descriptor.</summary>
public sealed record SourceLocationEntryDto(ushort Id, ushort FileId, uint Line, byte Kind, string Method);
