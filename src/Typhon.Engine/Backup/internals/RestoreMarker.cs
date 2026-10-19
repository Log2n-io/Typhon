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
/// <see cref="NewTimelineId"/> and deletes it (plan 1413 §2.8–2.9).
/// </summary>
internal sealed class RestoreMarkerFile
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; }

    public Guid DatabaseId { get; set; }

    public Guid NewTimelineId { get; set; }

    public DateTime RestoredUtc { get; set; }

    public string Tool { get; set; }

    public RestoredFrom RestoredFrom { get; set; }
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

        if (marker == null || marker.FormatVersion != RestoreMarkerFile.CurrentFormatVersion || marker.DatabaseId == Guid.Empty
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
