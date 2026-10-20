using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Typhon.Engine.Internals;

/// <summary><c>{store}/drills/{utc}-{id:D8}.json</c>: one drill, immutable (plan 1416 §4.6, DL-14).</summary>
internal sealed class DrillRecordFile
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; }

    public Guid DrillId { get; set; }

    public uint BackupId { get; set; }

    public Guid ChainId { get; set; }

    public Guid TimelineId { get; set; }

    public long PointLsn { get; set; }

    public DateTime StartedUtc { get; set; }

    public BackupDrillOutcome Outcome { get; set; }

    public BackupDrillStage FailedStage { get; set; }

    public string Error { get; set; }

    public long RestoreMs { get; set; }

    public long OpenMs { get; set; }

    public long CheckMs { get; set; }

    public int RecordsReplayed { get; set; }

    public int UowsPromoted { get; set; }

    public int UowsVoided { get; set; }

    public IntegrityVerdict Verdict { get; set; }

    public int Findings { get; set; }

    public string Host { get; set; }

    public string ToolVersion { get; set; }
}

/// <summary>
/// The drill history of a store (DL-14): one immutable file per drill, written with no lock (the engine may hold <c>store.lock</c> for a capture while a
/// drill runs), read newest-per-point. <c>catalog.json</c> stays a pure function of the packs. Retention deletes the records of the points it deletes.
/// </summary>
internal static class DrillRecords
{
    public const string Directory = "drills";

    /// <summary>
    /// What a store's drills say about its points: the newest drill, the newest that passed, and the points whose newest drill passed. A drill that ended
    /// in <see cref="BackupDrillOutcome.Error"/> is not counted: it could not run, and says nothing about its point.
    /// </summary>
    internal readonly record struct DrillHistory(BackupDrillSummary Newest, BackupDrillSummary NewestPassed, uint[] Passed,
        IReadOnlyDictionary<uint, BackupDrillSummary> PerPoint);

    public static string DirectoryOf(string storeRoot) => Path.Combine(storeRoot, Directory);

    /// <summary>Writes one record, <c>CreateNew</c> then flushed, its directory flushed: a drill that reported is on disk. Returns the file's path.</summary>
    public static string Write(IStoreFileSystem fs, string storeRoot, DrillRecordFile record)
    {
        var directory = DirectoryOf(storeRoot);
        if (!fs.DirectoryExists(directory))
        {
            fs.CreateDirectory(directory);
            fs.FlushDirectory(storeRoot);
        }

        var path = Path.Combine(directory, NameOf(record.StartedUtc, record.BackupId));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, BackupJsonContext.Default.DrillRecordFile);
        using (var file = fs.OpenCreateNew(path))
        {
            file.Write(bytes, 0);
            file.Flush();
        }

        fs.FlushDirectory(directory);
        return path;
    }

    /// <summary><c>{yyyyMMddTHHmmssfffZ}-{id:D8}.json</c>: name order is time order.</summary>
    public static string NameOf(DateTime startedUtc, uint backupId) =>
        startedUtc.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture) + "-" + backupId.ToString("D8", CultureInfo.InvariantCulture) + ".json";

    /// <summary>The point a record's name names, or 0 for a file that is not a record.</summary>
    public static uint PointOf(string name)
    {
        var dash = name.LastIndexOf('-');
        return name.EndsWith(".json", StringComparison.Ordinal) && dash > 0
               && uint.TryParse(name.AsSpan(dash + 1, name.Length - dash - 6), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? id
            : 0;
    }

    /// <summary>Every readable record, oldest first; an unreadable one is skipped (it reports nothing a later drill does not).</summary>
    public static List<DrillRecordFile> ReadAll(IStoreFileSystem fs, string storeRoot)
    {
        var records = new List<DrillRecordFile>();
        var directory = DirectoryOf(storeRoot);
        foreach (var f in fs.EnumerateFiles(directory))
        {
            if (PointOf(f.Name) == 0)
            {
                continue;
            }

            try
            {
                var bytes = StoreFileSystem.ReadAllBytes(fs, Path.Combine(directory, f.Name));
                var record = JsonSerializer.Deserialize(bytes, BackupJsonContext.Default.DrillRecordFile);
                if (record is { FormatVersion: DrillRecordFile.CurrentFormatVersion })
                {
                    records.Add(record);
                }
            }
            catch (Exception e) when (e is JsonException or IOException)
            {
            }
        }

        return records;
    }

    public static DrillHistory ReadNewestPerPoint(IStoreFileSystem fs, string storeRoot)
    {
        var perPoint = new Dictionary<uint, BackupDrillSummary>();
        BackupDrillSummary newest = null;
        BackupDrillSummary newestPassed = null;
        foreach (var r in ReadAll(fs, storeRoot))
        {
            if (r.Outcome == BackupDrillOutcome.Error)
            {
                continue;
            }

            var summary = new BackupDrillSummary(r.BackupId, r.StartedUtc, r.Outcome, r.Error);
            perPoint[r.BackupId] = summary;
            newest = summary;
            if (Passed(r.Outcome))
            {
                newestPassed = summary;
            }
        }

        var passed = new List<uint>();
        foreach (var (id, s) in perPoint)
        {
            if (Passed(s.Outcome))
            {
                passed.Add(id);
            }
        }

        passed.Sort();
        return new DrillHistory(newest, newestPassed, [.. passed], perPoint);
    }

    /// <summary>
    /// Deletes the records of points no longer in the store (retention, under <c>store.lock</c>). Best effort: a record left names a gone point.
    /// </summary>
    public static int DeleteForPointsNotIn(IStoreFileSystem fs, string storeRoot, HashSet<uint> listed)
    {
        var deleted = 0;
        var directory = DirectoryOf(storeRoot);
        foreach (var f in fs.EnumerateFiles(directory))
        {
            var id = PointOf(f.Name);
            if (id != 0 && !listed.Contains(id))
            {
                fs.DeleteFile(Path.Combine(directory, f.Name));
                deleted++;
            }
        }

        if (deleted > 0)
        {
            fs.FlushDirectory(directory);
        }

        return deleted;
    }

    private static bool Passed(BackupDrillOutcome outcome) => outcome is BackupDrillOutcome.Passed or BackupDrillOutcome.PassedWithWarnings;
}
