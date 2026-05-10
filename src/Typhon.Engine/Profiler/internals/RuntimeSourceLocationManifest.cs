using System;
using System.Collections.Generic;
using System.Threading;
using Typhon.Profiler;

namespace Typhon.Engine.Internals;

/// <summary>
/// Combines the compile-time source-location table emitted by <c>SourceLocationGenerator</c> (call-site attribution for <c>Begin*</c> factories) with
/// runtime-resolved entries for the scheduler's registered systems (PDB-derived in <see cref="SystemSourceResolver"/>). Exporters — both <c>FileExporter</c>
/// and <c>TcpExporter</c> — ship the merged manifest so the Workbench can resolve a span <em>and</em> a chunk's system to file:line through one
/// <c>SourceLocationManifestEntry</c> table.
/// </summary>
/// <remarks>
/// <para>
/// System entries use synthesized ids in the high half of the <c>ushort</c> space:
/// <c>id = SystemSourceIdMask | systemIndex</c>. Compile-time ids start at 1 and grow sequentially — they will not reach 0x8000 until ~32K distinct call sites
/// exist (we have ~hundreds today). This avoids a wire-format extension (a third trailer pointer) and lets the existing <c>useSourceLocationStore</c> on the
/// client resolve both span and chunk source through one lookup table.
/// </para>
/// <para>
/// File ids: when a system's PDB-resolved file path matches a compile-time entry, the system reuses that file id.
/// New paths are appended after the compile-time entries.
/// </para>
/// <para>
/// <b>Thread safety:</b> all public methods take <c>_lock</c>. <see cref="SetSystems"/> is called from a single <see cref="DagScheduler"/> construction;
/// concurrent scheduler builds are theoretical (no production path does this) but the lock makes the rebuild atomic. Readers (<see cref="BuildMerged"/>)
/// snapshot the merged arrays under the lock and return references — callers must not mutate the returned arrays. The result is memoized;
/// <see cref="SetSystems"/> invalidates the cache so the next <see cref="BuildMerged"/> rebuilds.
/// </para>
/// </remarks>
internal static class RuntimeSourceLocationManifest
{
    /// <summary>High bit reserved on <c>SourceLocationManifestEntry.Id</c> to flag a system entry.</summary>
    private const ushort SystemSourceIdMask = 0x8000;

    /// <summary>Compute the synthetic source-location id for a system's chunk-span source.</summary>
    private static ushort SystemSourceId(int systemIndex) => (ushort)(SystemSourceIdMask | (systemIndex & 0x7FFF));

    private static readonly Lock Lock = new();
    private static readonly List<string> RuntimeFiles = [];
    private static readonly Dictionary<string, ushort> RuntimeFileIds = new(StringComparer.Ordinal);
    private static readonly List<SourceLocationManifestEntry> SystemEntries = [];

    /// <summary>Memoized merged-manifest snapshot. Invalidated by <see cref="SetSystems"/> / <see cref="Clear"/>.</summary>
    private static (string[] Files, SourceLocationManifestEntry[] Entries)? CachedMerged;

    /// <summary>
    /// Replaces the runtime system table with entries derived from the given system definitions.
    /// Called once per <c>DagScheduler</c> construction. Clears the previous registration so repeat
    /// scheduler builds (in tests, multi-session hosts) do not accumulate stale entries.
    /// </summary>
    public static void SetSystems(IReadOnlyList<SystemDefinition> systems)
    {
        if (systems == null) return;
        lock (Lock)
        {
            RuntimeFiles.Clear();
            RuntimeFileIds.Clear();
            SystemEntries.Clear();
            CachedMerged = null;

            foreach (var sys in systems)
            {
                if (string.IsNullOrEmpty(sys?.SourceFilePath) || sys.SourceLine <= 0)
                {
                    continue;
                }
                var fileId = InternFile(sys.SourceFilePath);
                SystemEntries.Add(new SourceLocationManifestEntry(SystemSourceId(sys.Index), fileId, (uint)sys.SourceLine, 0, sys.SourceMethod ?? sys.Name ?? string.Empty));
            }
        }
    }

    /// <summary>
    /// Resets the runtime table. Intended for tests — production paths use <see cref="SetSystems"/>
    /// which already clears before re-registering.
    /// </summary>
    public static void Clear()
    {
        lock (Lock)
        {
            RuntimeFiles.Clear();
            RuntimeFileIds.Clear();
            SystemEntries.Clear();
            CachedMerged = null;
        }
    }

    /// <summary>
    /// Builds the merged file table + manifest entries for export. Compile-time entries come first
    /// to preserve the file-id contract used by <c>SourceLocations.All</c> (its <c>FileId</c> values
    /// are indices into the compile-time files array).
    /// </summary>
    public static (string[] Files, SourceLocationManifestEntry[] Entries) BuildMerged()
    {
        lock (Lock)
        {
            if (CachedMerged.HasValue) return CachedMerged.Value;

            var compileFiles = Typhon.Engine.Profiler.Generated.SourceLocations.Files ?? [];
            var compileEntries = Typhon.Engine.Profiler.Generated.SourceLocations.All ?? [];

            var files = new string[compileFiles.Length + RuntimeFiles.Count];
            Array.Copy(compileFiles, files, compileFiles.Length);
            for (var i = 0; i < RuntimeFiles.Count; i++)
            {
                files[compileFiles.Length + i] = RuntimeFiles[i];
            }

            var entries = new SourceLocationManifestEntry[compileEntries.Length + SystemEntries.Count];
            for (var i = 0; i < compileEntries.Length; i++)
            {
                var e = compileEntries[i];
                entries[i] = new SourceLocationManifestEntry(e.Id, e.FileId, (uint)e.Line, e.KindByte, e.Method ?? string.Empty);
            }
            for (var i = 0; i < SystemEntries.Count; i++)
            {
                entries[compileEntries.Length + i] = SystemEntries[i];
            }
            CachedMerged = (files, entries);
            return CachedMerged.Value;
        }
    }

    private static ushort InternFile(string path)
    {
        var compileFiles = Typhon.Engine.Profiler.Generated.SourceLocations.Files ?? [];
        for (ushort i = 0; i < compileFiles.Length; i++)
        {
            if (string.Equals(compileFiles[i], path, StringComparison.Ordinal))
            {
                return i;
            }
        }
        if (RuntimeFileIds.TryGetValue(path, out var existing))
        {
            return existing;
        }
        var fileId = (ushort)(compileFiles.Length + RuntimeFiles.Count);
        RuntimeFiles.Add(path);
        RuntimeFileIds[path] = fileId;
        return fileId;
    }
}
