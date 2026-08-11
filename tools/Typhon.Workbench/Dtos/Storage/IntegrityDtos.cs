namespace Typhon.Workbench.Dtos.Storage;

/// <summary>Request body for starting an integrity scan.</summary>
/// <param name="Path">Path to the <c>.typhon</c> bundle directory.</param>
/// <param name="Depth">spine | quick | standard | deep. Defaults to standard.</param>
public sealed record IntegrityScanRequestDto(string Path, string Depth = "standard");

/// <summary>Where a finding is, flattened for the client.</summary>
/// <param name="FilePageIndex">Physical page index, or -1.</param>
/// <param name="SegmentRootPage">Owning segment's root page, or -1.</param>
/// <param name="Kind">Owning segment kind.</param>
/// <param name="Archetype">Archetype name when resolvable.</param>
/// <param name="Component">Component name when resolvable.</param>
/// <param name="Text">Rendered, human-readable locus.</param>
public sealed record IntegrityLocusDto(int FilePageIndex, int SegmentRootPage, string Kind, string Archetype, string Component, string Text);

/// <summary>What a repair would cost, in user terms.</summary>
/// <param name="Kind">Unit of loss.</param>
/// <param name="Count">Rendered count — an exact number or an honest range.</param>
/// <param name="Archetype">Archetype the loss falls in, when resolvable.</param>
/// <param name="Component">Component the loss falls in, when resolvable.</param>
/// <param name="Explanation">Plain-English statement of what is no longer there.</param>
public sealed record IntegrityLossDto(string Kind, string Count, string Archetype, string Component, string Explanation);

/// <summary>One thing that is wrong with the database.</summary>
/// <param name="Code">Stable check code — treat it as an API, it is what an alert keys on.</param>
/// <param name="Severity">How bad it is.</param>
/// <param name="Confidence">Whether the source was quiescent when it was observed.</param>
/// <param name="Summary">One sentence, no jargon.</param>
/// <param name="Detail">The evidence.</param>
/// <param name="RuleId">The invariant violated.</param>
/// <param name="Repair">What can be done about it.</param>
/// <param name="Occurrences">How many times this fired.</param>
/// <param name="Locus">Where it is.</param>
/// <param name="Loss">What repairing it would cost.</param>
public sealed record IntegrityFindingDto(string Code, string Severity, string Confidence, string Summary, string Detail,
    string RuleId, string Repair, long Occurrences, IntegrityLocusDto Locus, IntegrityLossDto Loss);

/// <summary>Identity of the scanned database.</summary>
/// <param name="Name">Database name from page 0.</param>
/// <param name="FormatRevision">On-disk format revision.</param>
/// <param name="PageCount">Pages in the data file.</param>
/// <param name="SizeBytes">Data file size.</param>
/// <param name="CheckpointLsn">Last checkpoint LSN.</param>
/// <param name="CleanShutdown">Whether the last close set the clean flag.</param>
/// <param name="WalSegmentCount">WAL segment files present.</param>
/// <param name="WalBytes">Total WAL bytes.</param>
public sealed record IntegrityIdentityDto(string Name, int FormatRevision, int PageCount, long SizeBytes, long CheckpointLsn,
    bool CleanShutdown, int WalSegmentCount, long WalBytes);

/// <summary>What the scan looked at.</summary>
/// <param name="PagesScanned">Pages read and classified.</param>
/// <param name="PagesAllocated">Pages the bitmap marks in use.</param>
/// <param name="ChecksumFailures">Pages that failed verification.</param>
/// <param name="PagesWithSectorFooters">Pages carrying per-sector verification.</param>
/// <param name="SectorFailures">Sectors that failed across all pages.</param>
/// <param name="SegmentsWalked">Segments discovered and walked.</param>
/// <param name="BytesLeaked">Allocated-but-unreachable bytes.</param>
public sealed record IntegrityTotalsDto(int PagesScanned, int PagesAllocated, int ChecksumFailures, int PagesWithSectorFooters,
    int SectorFailures, int SegmentsWalked, long BytesLeaked);

/// <summary>What the scan could not have detected. Never omitted, including on a green report.</summary>
/// <param name="Structural">The always-true statement of the instrument's blind spot.</param>
/// <param name="ChecksSkipped">Checks not run at this depth.</param>
/// <param name="Caveats">Scan-specific caveats.</param>
public sealed record IntegrityLimitsDto(string Structural, IReadOnlyList<string> ChecksSkipped, IReadOnlyList<string> Caveats);

/// <summary>A complete integrity report.</summary>
/// <param name="Verdict">The one-word answer.</param>
/// <param name="ExitCode">The same verdict as a process exit code.</param>
/// <param name="Source">What was scanned.</param>
/// <param name="Mode">How the pages were reached.</param>
/// <param name="Depth">How much work was done.</param>
/// <param name="DurationMs">Wall-clock duration.</param>
/// <param name="Identity">Identity of the database.</param>
/// <param name="Totals">What the scan looked at.</param>
/// <param name="Findings">Everything that is wrong, severity-ranked.</param>
/// <param name="Limits">What the scan could not see.</param>
public sealed record IntegrityReportDto(string Verdict, int ExitCode, string Source, string Mode, string Depth, double DurationMs,
    IntegrityIdentityDto Identity, IntegrityTotalsDto Totals, IReadOnlyList<IntegrityFindingDto> Findings, IntegrityLimitsDto Limits);

/// <summary>One step of a repair plan.</summary>
/// <param name="Order">Execution position. The order is a correctness constraint.</param>
/// <param name="Action">What the step does.</param>
/// <param name="Class">Whether it regenerates or excises.</param>
/// <param name="Description">What will happen.</param>
/// <param name="Rationale">Why it is safe, or what it costs.</param>
/// <param name="Addresses">Check codes it answers.</param>
public sealed record RepairStepDto(int Order, string Action, string Class, string Description, string Rationale, IReadOnlyList<string> Addresses);

/// <summary>A repair plan, ready for an operator to review.</summary>
/// <param name="Source">The database it targets.</param>
/// <param name="Fingerprint">Binds the plan to the exact state it was built for; applying refuses on drift.</param>
/// <param name="Verdict">The diagnosis it addresses.</param>
/// <param name="RequiresLossyConsent">Whether any step destroys something.</param>
/// <param name="Steps">The ordered steps.</param>
/// <param name="Loss">The full loss enumeration. This is the consent, not a summary of it.</param>
/// <param name="Unaddressed">Findings this build cannot repair, with the reason and the escalation path.</param>
/// <param name="BlockedReason">
/// Why applying is refused outright, or <c>null</c>. Distinct from an empty step list: empty means nothing needs repairing,
/// blocked means this build must not be the one to try.
/// </param>
public sealed record RepairPlanDto(string Source, string Fingerprint, string Verdict, bool RequiresLossyConsent,
    IReadOnlyList<RepairStepDto> Steps, IReadOnlyList<IntegrityLossDto> Loss, IReadOnlyList<string> Unaddressed,
    string BlockedReason);

/// <summary>Request body for applying a repair.</summary>
/// <param name="Path">Path to the bundle.</param>
/// <param name="Fingerprint">The plan's fingerprint. Must still match, or the request is refused.</param>
/// <param name="AllowLoss">Consent to lossy steps.</param>
/// <param name="BackupFirst">Copy the bundle before the first mutation.</param>
/// <param name="DryRun">Describe every step and execute none.</param>
public sealed record RepairApplyRequestDto(string Path, string Fingerprint, bool AllowLoss = false, bool BackupFirst = true, bool DryRun = false);

/// <summary>What happened to one step.</summary>
/// <param name="Order">The step's position.</param>
/// <param name="Action">What it was going to do.</param>
/// <param name="Outcome">What happened.</param>
/// <param name="Detail">What it actually did, or why it did not.</param>
public sealed record RepairStepResultDto(int Order, string Action, string Outcome, string Detail);

/// <summary>The receipt for an applied repair.</summary>
/// <param name="Succeeded">Whether every attempted step worked.</param>
/// <param name="BackupPath">Where the pre-repair copy went, when one was taken.</param>
/// <param name="Results">Per-step receipts.</param>
/// <param name="Verification">The scan run after the repair.</param>
public sealed record RepairOutcomeDto(bool Succeeded, string BackupPath, IReadOnlyList<RepairStepResultDto> Results, IntegrityReportDto Verification);
