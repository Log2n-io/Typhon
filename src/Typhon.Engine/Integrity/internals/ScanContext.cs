using System;
using System.Collections.Generic;

namespace Typhon.Engine.Internals;

/// <summary>
/// Everything one scan discovered, shared by every check in the catalogue.
/// </summary>
/// <remarks>
/// Built once by the structural pass so no check re-walks the file, and so every check sees the <i>same</i> view of a
/// damaged database. Two checks that independently re-derive ownership can disagree, and on a corrupt file they will —
/// which would make findings depend on evaluation order.
/// </remarks>
internal sealed class ScanContext
{
    /// <summary>The page source under inspection.</summary>
    public IPageSource Source { get; init; }

    /// <summary>Scan options, including depth and check filters.</summary>
    public IntegrityOptions Options { get; init; }

    /// <summary>How the pages were reached, which bounds the confidence of every cross-structure conclusion.</summary>
    public ScanMode Mode { get; init; }

    /// <summary>The bundle, when the source is one. <c>null</c> for non-bundle sources.</summary>
    public OfflineBundlePageSource Bundle { get; init; }

    /// <summary>Page-0 identity and bootstrap dictionary.</summary>
    public BootstrapView Bootstrap { get; set; }

    /// <summary>Every segment discovered by the physical sweep, keyed by root page.</summary>
    public Dictionary<int, SegmentView> Segments { get; } = [];

    /// <summary>Per-page role, indexed by file-page index.</summary>
    public PageRole[] Roles { get; set; } = [];

    /// <summary>Per-page owning segment root, or <c>-1</c> when unowned. Indexed by file-page index.</summary>
    public int[] Owner { get; set; } = [];

    /// <summary>Per-page flags byte as read, indexed by file-page index.</summary>
    public byte[] FlagsByte { get; set; } = [];

    /// <summary>
    /// Physical slots that are the shadow half of an A/B protected directory page. Tracked separately from
    /// <see cref="Roles"/> because a twin can also be one of the genesis-reserved pages, and the role array keeps the
    /// stronger classification — so the role alone cannot answer "is this a twin?".
    /// </summary>
    public HashSet<int> TwinSlots { get; } = [];

    /// <summary>The page-allocation bitmap, or <c>null</c> when it could not be read.</summary>
    public OccupancyView Occupancy { get; set; }

    /// <summary>
    /// The database's own schema manifest, or <c>null</c> when it was not read (shallow depths) or not recoverable.
    /// </summary>
    /// <remarks>
    /// Every cross-structure check hangs off this. It is what turns a bag of segments into named archetypes with
    /// component counts, entity-key watermarks and cluster/EntityMap roots — and it is read from the file, not from a
    /// schema assembly, which is the correction recorded in <c>09 §1.1</c>.
    /// </remarks>
    public SchemaCatalogReader Manifest { get; set; }

    /// <summary>
    /// Live entity ids per archetype name, as read from cluster occupancy. Populated by <c>ClusterChecks</c>.
    /// </summary>
    /// <remarks>
    /// Shared rather than re-derived so <c>MAP-01</c>/<c>MAP-02</c> compare against the SAME view of the cluster the
    /// <c>CLU</c> checks reported on. Two walks that independently re-read a damaged cluster can disagree, and then a
    /// finding depends on which check ran first.
    /// </remarks>
    public Dictionary<string, HashSet<long>> ClusterEntityIds { get; } = [];

    /// <summary>
    /// Where each live entity actually sits, as a packed <c>ClusterLocation</c>, per archetype. Populated by
    /// <c>ClusterChecks</c>.
    /// </summary>
    /// <remarks>
    /// What turns <c>MAP-01</c> from <i>"this identity exists somewhere"</i> into the catalogue's actual claim —
    /// <i>"the entry resolves to an occupied slot"</i>. An EntityMap value record carries a cluster chunk id and a slot
    /// index, so an entry can name a real entity and still point at the wrong slot; only comparing the location catches
    /// that, and it is the shape a rebuild over stale state leaves behind.
    /// </remarks>
    public Dictionary<string, Dictionary<long, int>> ClusterEntityLocations { get; } = [];

    /// <summary>Findings accumulated so far.</summary>
    public FindingCollector Findings { get; init; }

    /// <summary>Pages read and classified.</summary>
    public int PagesScanned { get; set; }

    /// <summary>Pages whose stored checksum did not match.</summary>
    public int ChecksumFailures { get; set; }

    /// <summary>Pages carrying a per-sector verification footer.</summary>
    public int PagesWithSectorFooters { get; set; }

    /// <summary>Sectors that failed verification across the whole file.</summary>
    public int SectorFailures { get; set; }

    /// <summary>Set when a <see cref="IntegritySeverity.Fatal"/> finding means later checks cannot be trusted.</summary>
    public bool StopScan { get; set; }

    /// <summary>Whether cross-structure conclusions from this source can be trusted as observed rather than inferred.</summary>
    public IntegrityConfidence Confidence => Mode == ScanMode.OnlineSampled ? IntegrityConfidence.Suspected : IntegrityConfidence.Confirmed;

    /// <summary>Whether a page index addresses a page that exists in the source.</summary>
    /// <param name="filePageIndex">The page index to test.</param>
    public bool IsInRange(int filePageIndex) => filePageIndex >= 0 && filePageIndex < Source.PageCount;

    /// <summary>Whether the requested depth includes a given depth level.</summary>
    /// <param name="depth">The depth level the check needs.</param>
    public bool AtLeast(ScanDepth depth) => Options.Depth >= depth;

    /// <summary>
    /// Resolves the segment kind that owns a page, for the <see cref="Locus"/> of a page-scoped finding.
    /// </summary>
    /// <param name="filePageIndex">The page whose owner is wanted.</param>
    public Locus LocusForPage(int filePageIndex)
    {
        if (filePageIndex < 0 || filePageIndex >= Owner.Length)
        {
            return new Locus(filePageIndex);
        }

        var root = Owner[filePageIndex];
        if (root < 0 || !Segments.TryGetValue(root, out var seg))
        {
            return new Locus(filePageIndex);
        }

        return new Locus(filePageIndex, root, seg.Kind);
    }

    /// <summary>Reports a finding if its check code passes the option filters.</summary>
    /// <param name="finding">The finding to record.</param>
    public void Report(IntegrityFinding finding)
    {
        if (!Options.IsCheckEnabled(finding.Code))
        {
            return;
        }

        Findings.Add(finding);
    }

    /// <summary>
    /// Reports a finding built from the common fields, stamping this scan's confidence automatically so no check can
    /// accidentally claim a live observation was quiescent.
    /// </summary>
    /// <param name="code">Stable check code.</param>
    /// <param name="severity">How bad it is.</param>
    /// <param name="ruleId">The <c>rules/</c> invariant violated.</param>
    /// <param name="locus">Where it is.</param>
    /// <param name="summary">One sentence, no jargon.</param>
    /// <param name="detail">The evidence.</param>
    /// <param name="repair">What can be done about it.</param>
    /// <param name="loss">What repairing it would cost.</param>
    public void Report(string code, IntegritySeverity severity, string ruleId, Locus locus, string summary, string detail,
        Repairability repair = Repairability.NotRepairable, LossEstimate loss = null)
        => Report(new IntegrityFinding
        {
            Code = code,
            Severity = severity,
            RuleId = ruleId,
            Locus = locus,
            Summary = summary,
            Detail = detail,
            Repair = repair,
            Loss = loss ?? LossEstimate.None,
            Confidence = Confidence
        });
}
