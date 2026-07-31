using System;
using System.Globalization;
using System.IO;

namespace Typhon.Engine;

/// <summary>
/// Knows where a database's profiling captures live: <c>{name}.typhon/profilings/</c> (#616, design D-1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why co-location.</b> Putting captures inside the bundle makes correlation <i>structural</i> — given a capture, its database is two levels up. The
/// alternative is inferring the pairing from a fingerprint, and every inference step is a place to be subtly wrong later. Nothing else in the engine needs
/// teaching about the directory: bundle handling never enumerates the root and rejects unknown entries (<see cref="PagedMMF"/> only guards against a *file*
/// occupying the bundle path), and WAL discovery is pattern-scoped, so a new subdirectory trips nothing.
/// </para>
/// <para>
/// ⚠️ <b>Co-location is not provenance.</b> A capture sitting here proves it was <i>written</i> here, not that it matches what is here now — the bundle may
/// have been copied, restored or migrated since. The identity recorded in the trace header (#614) and the drift readout remain required; this type only
/// decides where files go.
/// </para>
/// <para>Every path in the feature is derived here rather than by re-joining <c>"profilings"</c> at each call site.</para>
/// </remarks>
public static class TraceLocation
{
    /// <summary>Subdirectory of the database bundle holding its profiling captures.</summary>
    public const string ProfilingsDirectoryName = "profilings";

    /// <summary>Extension of a capture file.</summary>
    public const string TraceExtension = ".typhon-trace";

    /// <summary>
    /// Glob matching the derived sidecar caches a viewer builds beside captures. Regenerable, so retention reclaims these first and never lets them compete
    /// with real captures for budget. The shape follows <see cref="Typhon.Profiler.TraceFileCacheConstants.CacheFileExtension"/> — the suffix is appended to
    /// the whole capture path, not substituted for its extension.
    /// </summary>
    public const string SidecarSearchPattern = "*" + TraceExtension + Typhon.Profiler.TraceFileCacheConstants.CacheFileExtension;

    /// <summary>The <c>profilings/</c> directory for a database bundle. Does not create it — see <see cref="NewCapturePath"/>.</summary>
    /// <param name="bundleDirectory">The database's <c>{name}.typhon</c> directory.</param>
    public static string ProfilingsDirectoryOf(string bundleDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(bundleDirectory);
        return Path.Combine(bundleDirectory, ProfilingsDirectoryName);
    }

    /// <summary>
    /// Path for a capture starting now, creating <c>profilings/</c> if needed. The name is a UTC timestamp to the millisecond, which sorts
    /// lexicographically in chronological order — so a directory listing is already the right order for a profiles list, with no parsing.
    /// </summary>
    /// <param name="bundleDirectory">The database's <c>{name}.typhon</c> directory.</param>
    /// <param name="startedUtc">Capture start time; defaults to now. Injectable so tests are not timing-dependent.</param>
    public static string NewCapturePath(string bundleDirectory, DateTime startedUtc = default)
    {
        var directory = ProfilingsDirectoryOf(bundleDirectory);
        Directory.CreateDirectory(directory);

        var stamp = (startedUtc == default ? DateTime.UtcNow : startedUtc).ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        return Path.Combine(directory, stamp + TraceExtension);
    }

    /// <summary>
    /// The sidecar cache path for a capture. Delegates to <see cref="Typhon.Profiler.TraceFileCacheBuilder.GetCachePathFor"/> rather than re-deriving the
    /// convention: the builder and the reader already agree on it, and a pruner that computed the name a second way would silently miss real sidecars the day
    /// the convention moved.
    /// </summary>
    public static string SidecarOf(string capturePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(capturePath);
        return Typhon.Profiler.TraceFileCacheBuilder.GetCachePathFor(capturePath);
    }

    /// <summary>
    /// The capture a sidecar was derived from — the inverse of <see cref="SidecarOf"/>. Returns <paramref name="sidecarPath"/> unchanged when it does not
    /// carry the sidecar suffix.
    /// </summary>
    public static string CaptureOfSidecar(string sidecarPath)
    {
        const string suffix = Typhon.Profiler.TraceFileCacheConstants.CacheFileExtension;
        return sidecarPath != null && sidecarPath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? sidecarPath[..^suffix.Length] : sidecarPath;
    }

    /// <summary>
    /// True when <paramref name="path"/> names a capture rather than a sidecar or the policy file.
    /// </summary>
    /// <remarks>
    /// The sidecar suffix is <i>appended</i> to the whole capture name (<c>x.typhon-trace</c> → <c>x.typhon-trace-cache</c>), so a sidecar does not end with
    /// the capture extension and the first test already excludes it. The explicit second test is belt-and-braces against that convention changing to a
    /// substituted extension, where the two would become indistinguishable by suffix alone.
    /// </remarks>
    public static bool IsCapture(string path) =>
        path != null
        && path.EndsWith(TraceExtension, StringComparison.OrdinalIgnoreCase)
        && !path.EndsWith(Typhon.Profiler.TraceFileCacheConstants.CacheFileExtension, StringComparison.OrdinalIgnoreCase);
}
