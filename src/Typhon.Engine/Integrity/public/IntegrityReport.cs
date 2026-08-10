using JetBrains.Annotations;
using System;
using System.Collections.Generic;

namespace Typhon.Engine;

/// <summary>
/// The one-word answer to "is my database sound?". A single grep-able token, because an operator scanning a fleet needs
/// exactly that.
/// </summary>
[PublicAPI]
public enum IntegrityVerdict
{
    /// <summary>Every check passed.</summary>
    Sound = 0,

    /// <summary>
    /// No correctness problem, but space is allocated and unreachable. Separate from <see cref="Sound"/> deliberately:
    /// leaks must not scare anyone into a repair, but hiding them inside <see cref="Sound"/> makes space growth inexplicable.
    /// </summary>
    SoundWithLeaks = 1,

    /// <summary>A derived structure disagrees with primary data. Schedule a repair; nothing is lost.</summary>
    Divergent = 2,

    /// <summary>Primary data is unreadable. Stop and think before repairing.</summary>
    DataLoss = 3,

    /// <summary>The database cannot be traversed. The scan stopped early.</summary>
    Unopenable = 4
}

/// <summary>How the scan reached its pages, which bounds what its conclusions are worth.</summary>
[PublicAPI]
public enum ScanMode
{
    /// <summary>Read the bundle as bytes with no engine, no lock, no replay. Conclusions are <see cref="IntegrityConfidence.Confirmed"/>.</summary>
    Offline = 0,

    /// <summary>Read through a running engine without quiescing it. Per-page checks only; conclusions are <see cref="IntegrityConfidence.Suspected"/>.</summary>
    OnlineSampled = 1,

    /// <summary>Read through a running engine behind a tick fence and checkpoint barrier. Conclusions are <see cref="IntegrityConfidence.Confirmed"/>.</summary>
    OnlineQuiesced = 2
}

/// <summary>How much work a scan does, and therefore what it can conclude.</summary>
[PublicAPI]
public enum ScanDepth
{
    /// <summary>
    /// Page-0 pair selection, the bootstrap stream, and that every segment pointer resolves. Bounded by the number of
    /// <i>segments</i>, not pages — kilobytes read, sub-millisecond. Cheap enough to run on every open.
    /// </summary>
    Spine = 0,

    /// <summary>Adds every page header. O(pages), IOPS-bound; no page bodies are hashed.</summary>
    Quick = 1,

    /// <summary>Adds a checksum sweep over every allocated page. O(pages), bandwidth-bound.</summary>
    Standard = 2,

    /// <summary>Adds cross-structure checks — chains, clusters, indexes, entity maps, allocators. O(entities).</summary>
    Deep = 3
}

/// <summary>Identity of the scanned database, as read from its own bytes.</summary>
[PublicAPI]
public sealed class DatabaseIdentity
{
    /// <summary>Database name recorded on page 0, or <c>null</c> when unreadable.</summary>
    public string Name { get; init; }

    /// <summary>On-disk format revision recorded on page 0.</summary>
    public int FormatRevision { get; init; }

    /// <summary>Total pages in the data file.</summary>
    public int PageCount { get; init; }

    /// <summary>Size of the data file in bytes.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Last checkpoint LSN recorded in the bootstrap dictionary, or <c>0</c>.</summary>
    public long CheckpointLsn { get; init; }

    /// <summary>Whether the previous process recorded a clean shutdown. <c>false</c> means recovery would run on open.</summary>
    public bool CleanShutdown { get; init; }

    /// <summary>Which physical slot of the page-0 A/B meta pair held the selected content, or <c>-1</c> when neither was valid.</summary>
    public int MetaSlot { get; init; } = -1;

    /// <summary>Generation of the selected meta slot.</summary>
    public ulong MetaGeneration { get; init; }

    /// <summary>Number of WAL segment files present beside the data file.</summary>
    public int WalSegmentCount { get; init; }

    /// <summary>Total bytes across the WAL segment files.</summary>
    public long WalBytes { get; init; }
}

/// <summary>Aggregate counters describing what the scan actually looked at.</summary>
[PublicAPI]
public sealed class ScanTotals
{
    /// <summary>Pages read and classified.</summary>
    public int PagesScanned { get; init; }

    /// <summary>Pages whose occupancy bit is set.</summary>
    public int PagesAllocated { get; init; }

    /// <summary>Pages whose stored checksum did not match their content.</summary>
    public int ChecksumFailures { get; init; }

    /// <summary>Pages carrying a per-sector verification footer.</summary>
    public int PagesWithSectorFooters { get; init; }

    /// <summary>Sectors that failed verification across all pages.</summary>
    public int SectorFailures { get; init; }

    /// <summary>Logical segments discovered and walked.</summary>
    public int SegmentsWalked { get; init; }

    /// <summary>Chunks found allocated across every chunk-based segment.</summary>
    public long ChunksAllocated { get; init; }

    /// <summary>Bytes held by allocated-but-unreachable structures.</summary>
    public long BytesLeaked { get; init; }

    /// <summary>Finding counts by severity, indexed by <see cref="IntegritySeverity"/>.</summary>
    public IReadOnlyList<int> BySeverity { get; init; } = [];
}

/// <summary>
/// The statement of what a scan could <b>not</b> have detected. Present on every report, including a fully green one, and
/// deliberately not suppressible.
/// </summary>
/// <remarks>
/// When a recovery gap discards a committed update, the database passes <b>every</b> check in the catalogue — because it
/// <i>is</i> self-consistent, merely stale. A report that says <c>Sound</c> without this block is telling the operator
/// something materially untrue, and the whole feature is a claim to honesty.
/// </remarks>
[PublicAPI]
public sealed class ScanLimits
{
    /// <summary>The always-true statement of the instrument's structural blind spot.</summary>
    public const string StructuralLimit =
        "This scan verifies that the database is INTERNALLY CONSISTENT. It cannot verify that the database matches what "
        + "was committed. Specifically it cannot detect: committed updates lost during a prior recovery; entities "
        + "resurrected by a prior recovery; or any damage that left every structure in mutual agreement. Detecting those "
        + "requires a reference copy, which this build cannot take.";

    /// <summary>Check codes that were not run at the requested depth or were explicitly excluded.</summary>
    public IReadOnlyList<string> ChecksSkipped { get; init; } = [];

    /// <summary>Additional, scan-specific caveats — a truncated walk, an unresolvable schema, an unstamped page range.</summary>
    public IReadOnlyList<string> Caveats { get; init; } = [];
}

/// <summary>
/// The product of a scan: what was looked at, what is wrong, what a repair would cost, and what could not be seen.
/// </summary>
/// <remarks>
/// This is also the interface the crash suite consumes. It needs no shadow model, so it can be asserted after any chaos
/// cell — including ones whose correct final state the harness cannot compute.
/// </remarks>
[PublicAPI]
public sealed class IntegrityReport
{
    /// <summary>Schema version of this report's JSON form. Bumped on any breaking shape change.</summary>
    public const int ReportVersion = 1;

    /// <summary>Identity of the source that was scanned, from <see cref="IPageSource.Describe"/>.</summary>
    public required string Source { get; init; }

    /// <summary>How the pages were reached.</summary>
    public required ScanMode Mode { get; init; }

    /// <summary>How much work was done.</summary>
    public required ScanDepth Depth { get; init; }

    /// <summary>Identity of the scanned database.</summary>
    public required DatabaseIdentity Identity { get; init; }

    /// <summary>Every finding, severity-ranked.</summary>
    public required IReadOnlyList<IntegrityFinding> Findings { get; init; }

    /// <summary>What the scan looked at.</summary>
    public required ScanTotals Totals { get; init; }

    /// <summary>What the scan could not have detected. Never <c>null</c>.</summary>
    public required ScanLimits Limits { get; init; }

    /// <summary>Wall-clock duration of the scan.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>UTC instant the scan completed.</summary>
    public DateTime CompletedUtc { get; init; } = DateTime.UtcNow;

    /// <summary>The one-word answer, derived from the most severe finding.</summary>
    public IntegrityVerdict Verdict
    {
        get
        {
            var worst = IntegritySeverity.Advisory;
            var any = false;
            for (var i = 0; i < Findings.Count; i++)
            {
                var s = Findings[i].Severity;
                if (!any || s < worst)
                {
                    worst = s;
                    any = true;
                }
            }

            if (!any)
            {
                return IntegrityVerdict.Sound;
            }

            return worst switch
            {
                IntegritySeverity.Fatal => IntegrityVerdict.Unopenable,
                IntegritySeverity.DataLoss => IntegrityVerdict.DataLoss,
                IntegritySeverity.Divergence => IntegrityVerdict.Divergent,
                IntegritySeverity.Leak => IntegrityVerdict.SoundWithLeaks,
                _ => IntegrityVerdict.Sound
            };
        }
    }

    /// <summary>
    /// Process exit code carrying the verdict, so <c>typhon check</c> drops into a cron job or a CI gate without anyone
    /// parsing anything. Distinct codes for <see cref="IntegrityVerdict.Divergent"/> and
    /// <see cref="IntegrityVerdict.DataLoss"/> matter: the first is "schedule a repair", the second is "stop and think".
    /// </summary>
    public int ExitCode => (int)Verdict;

    /// <summary>Aggregate loss across every finding, grouped by archetype and component.</summary>
    public IReadOnlyList<LossEstimate> LossSummary
    {
        get
        {
            var byKey = new Dictionary<string, LossEstimate>(StringComparer.Ordinal);
            for (var i = 0; i < Findings.Count; i++)
            {
                var loss = Findings[i].Loss;
                if (loss == null || loss.IsNone)
                {
                    continue;
                }

                var key = $"{loss.Archetype} {loss.Component} {loss.Kind}";
                if (!byKey.TryGetValue(key, out var acc))
                {
                    byKey[key] = loss;
                    continue;
                }

                byKey[key] = new LossEstimate
                {
                    Kind = acc.Kind,
                    EntityCount = acc.EntityCount >= 0 && loss.EntityCount >= 0 ? acc.EntityCount + loss.EntityCount : -1,
                    BoundedMin = acc.BoundedMin + loss.BoundedMin,
                    BoundedMax = acc.BoundedMax + loss.BoundedMax,
                    Archetype = acc.Archetype,
                    Component = acc.Component,
                    Explanation = acc.Explanation,
                    Sample = acc.Sample
                };
            }

            var result = new List<LossEstimate>(byKey.Count);
            foreach (var v in byKey.Values)
            {
                result.Add(v);
            }

            return result;
        }
    }

    /// <summary>Whether the database is free of correctness problems (leaks and advisories do not count).</summary>
    public bool IsSound => Verdict is IntegrityVerdict.Sound or IntegrityVerdict.SoundWithLeaks;
}
