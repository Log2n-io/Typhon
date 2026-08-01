namespace Typhon.Workbench.Dtos.Databases;

/// <summary>
/// One database the machine-local registry knows about (#622, design D-7).
/// </summary>
/// <param name="Name">The database name — the bundle directory's stem.</param>
/// <param name="BundlePath">Absolute path of the <c>{name}.typhon</c> bundle. This is the registry's key and what an open request takes.</param>
/// <param name="DatabaseId">The database's durable identity, so a path reused by a recreated database is distinguishable from the original.</param>
/// <param name="FirstSeenUtc">When this machine first recorded it.</param>
/// <param name="LastOpenedUtc">When it was last opened, by any Typhon process.</param>
/// <param name="LastOpenedBy">The entry assembly of the process that last opened it.</param>
/// <param name="Exists">Whether the bundle is still on disk — recomputed on every listing, never stored.</param>
public record KnownDatabaseDto(
    string Name,
    string BundlePath,
    Guid DatabaseId,
    DateTime FirstSeenUtc,
    DateTime LastOpenedUtc,
    string LastOpenedBy,
    bool Exists);

/// <summary>
/// The registry's whole state: its entries, and whether it is even switched on.
/// </summary>
/// <remarks>
/// <see cref="Enabled"/> and <see cref="DisabledReason"/> are not decoration. D-7 warns that "an empty list teaches the user the feature is useless and they
/// stop looking" — so a registry that is switched off must be distinguishable from one that has simply seen nothing yet, and the client cannot tell those apart
/// from an empty array. The reason names the responsible switch so the user can undo it.
/// </remarks>
/// <param name="Enabled">Whether new opens are being recorded.</param>
/// <param name="DisabledReason">Which switch turned it off, or <c>null</c> when enabled.</param>
/// <param name="RegistryDirectory">Where the entries live — shown so a user can inspect or delete them by hand.</param>
/// <param name="Entries">Known databases, most-recently-opened first.</param>
public record KnownDatabaseListDto(
    bool Enabled,
    string DisabledReason,
    string RegistryDirectory,
    IReadOnlyList<KnownDatabaseDto> Entries);
