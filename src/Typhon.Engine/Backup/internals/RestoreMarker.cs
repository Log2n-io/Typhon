using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Typhon.Engine.Internals;

/// <summary>Where a restored bundle came from.</summary>
internal sealed class RestoredFrom
{
    public Guid StoreId { get; set; }

    public Guid ChainId { get; set; }

    public uint BackupId { get; set; }

    public BackupKind Kind { get; set; }

    public long CheckpointLsn { get; set; }

    public long PointLsn { get; set; }

    public Guid TimelineId { get; set; }
}

/// <summary>
/// <c>restore.json</c>: written by a restore inside its private staging directory, read by the first open of the restored bundle, which adopts
/// <see cref="NewTimelineId"/> and deletes it (plan 1413 §2.8–2.9). Version 2 (#1418) adds the restore-to-LSN fields and
/// <see cref="ForceCrashRecovery"/>: a build that reads version 1 only refuses it rather than misread it.
/// </summary>
internal sealed class RestoreMarkerFile
{
    public const int CurrentFormatVersion = 2;

    /// <summary>The oldest version this build reads: a version-1 marker is a point restore, its new fields at their defaults.</summary>
    public const int MinFormatVersion = 1;

    public int FormatVersion { get; set; }

    public Guid DatabaseId { get; set; }

    public Guid NewTimelineId { get; set; }

    public DateTime RestoredUtc { get; set; }

    public string Tool { get; set; }

    public RestoredFrom RestoredFrom { get; set; }

    /// <summary>The LSN the restore was asked for; 0 for a point restore.</summary>
    public long TargetLsn { get; set; }

    /// <summary>
    /// The last LSN the restored WAL holds: <c>L'</c>, the highest frame end at or below <see cref="TargetLsn"/>; the point's <c>P</c> for a point restore
    /// (0 in a version-1 marker: read <see cref="RestoredFrom.PointLsn"/>). The restored timeline's origin.
    /// </summary>
    public long EffectiveLsn { get; set; }

    /// <summary>
    /// Open on the crash path even though the image was closed cleanly (WA-12): a restore to an LSN past a clean offline point's own WAL needs its archived
    /// WAL replayed, which the clean path never reads.
    /// </summary>
    public bool ForceCrashRecovery { get; set; }

    /// <summary>The restored timeline's origin: <see cref="EffectiveLsn"/>, or the point's <c>P</c> for a version-1 marker.</summary>
    public long OriginLsn => EffectiveLsn != 0 ? EffectiveLsn : RestoredFrom?.PointLsn ?? 0;
}

/// <summary><c>restore.staging</c>: marks a <c>{target}.restoring</c> directory as a restore's own, so a leftover is recognisably safe to delete.</summary>
internal sealed class RestoreStagingFile
{
    public Guid RestoreId { get; set; }

    public DateTime StartedUtc { get; set; }

    public int Pid { get; set; }
}

/// <summary>The outcome of looking for a restore marker.</summary>
internal enum RestoreMarkerState : byte
{
    Absent,
    Valid,
    Invalid,
}

/// <summary>Reads and writes <c>restore.json</c>.</summary>
internal static class RestoreMarker
{
    public const string FileName = "restore.json";
    public const string StagingFileName = "restore.staging";

    public static byte[] Serialize(RestoreMarkerFile marker) => JsonSerializer.SerializeToUtf8Bytes(marker, RestoreJsonContext.Default.RestoreMarkerFile);

    /// <summary>
    /// Reads <c>{bundle}/restore.json</c>. <see cref="RestoreMarkerState.Absent"/> on every normal open (one existence probe);
    /// <see cref="RestoreMarkerState.Invalid"/> with the reason when it exists but cannot be trusted — the open must refuse then, never ignore it.
    /// </summary>
    public static RestoreMarkerState TryRead(IStoreFileSystem fs, string bundleDirectory, out RestoreMarkerFile marker, out string problem)
    {
        marker = null;
        problem = null;
        var path = System.IO.Path.Combine(bundleDirectory, FileName);
        if (!fs.FileExists(path))
        {
            return RestoreMarkerState.Absent;
        }

        try
        {
            marker = JsonSerializer.Deserialize(StoreFileSystem.ReadAllBytes(fs, path), RestoreJsonContext.Default.RestoreMarkerFile);
        }
        catch (Exception ex) when (ex is JsonException or System.IO.IOException or UnauthorizedAccessException)
        {
            problem = ex.Message;
            return RestoreMarkerState.Invalid;
        }

        if (marker == null || marker.FormatVersion < RestoreMarkerFile.MinFormatVersion || marker.FormatVersion > RestoreMarkerFile.CurrentFormatVersion
            || marker.DatabaseId == Guid.Empty
            || marker.NewTimelineId == Guid.Empty || marker.RestoredFrom == null)
        {
            problem = marker == null ? "empty" : $"format {marker.FormatVersion}, or a field is missing";
            marker = null;
            return RestoreMarkerState.Invalid;
        }

        return RestoreMarkerState.Valid;
    }
}

/// <summary>Source-generated serialisation of the restore's JSON files (#409).</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(RestoreMarkerFile))]
[JsonSerializable(typeof(RestoreStagingFile))]
internal sealed partial class RestoreJsonContext : JsonSerializerContext;
