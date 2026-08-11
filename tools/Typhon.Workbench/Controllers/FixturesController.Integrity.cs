#if DEBUG
using Microsoft.AspNetCore.Mvc;
using Typhon.Engine;

namespace Typhon.Workbench.Controllers;

/// <summary>
/// DEBUG-only damage injection for the integrity Playwright canaries (#729).
/// </summary>
/// <remarks>
/// <para>
/// Without this the integrity e2e could only ever assert the green path — scan a healthy database, see
/// <c>Sound</c>. That is the one outcome whose regression is least interesting, and the whole feature exists
/// for the other outcomes. Every branch worth protecting (a verdict banner that isn't green, a findings table
/// with rows in it, a repair plan with steps, a consent gate, an applied receipt) is unreachable from a
/// healthy fixture.
/// </para>
/// <para>
/// It damages an <b>existing</b> bundle rather than building one, so it composes with the sample-database
/// endpoint instead of duplicating its generator. Gated by <c>#if DEBUG</c> for the obvious reason: an
/// endpoint that corrupts a database on request has no business in a shipped binary.
/// </para>
/// </remarks>
public sealed partial class FixturesController
{
    /// <summary>
    /// Corrupts a bundle in a named, reproducible way and returns the verdict the damage produces.
    /// </summary>
    /// <param name="req">Which bundle, and which damage variant.</param>
    [HttpPost("damaged")]
    public ActionResult<DamagedFixtureResponseDto> CreateDamaged([FromBody] DamagedFixtureRequestDto req)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.Path))
        {
            return BadRequest(new { error = "A bundle path is required." });
        }

        string bundle;
        try
        {
            bundle = OfflineBundlePageSource.ResolveBundleDirectory(req.Path);
        }
        catch (DirectoryNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }

        var variant = string.IsNullOrWhiteSpace(req.Variant) ? "meta-slot" : req.Variant.Trim().ToLowerInvariant();

        try
        {
            switch (variant)
            {
                // One meta slot clobbered. The A/B pair means the other slot still carries a valid image, so this
                // is Class A repairable *without loss* — the flow the canary drives end to end (plan has steps,
                // no consent gate, apply, verify green).
                case "meta-slot":
                    DamagePage(bundle, 0, page => page.AsSpan(200, 64).Fill(0xAB));
                    break;

                // A flipped byte inside a data page: fails its CRC, and the data it held is simply gone. Produces
                // a DataLoss verdict — the canary for a report that is not green and a repair that costs something.
                case "checksum":
                {
                    var lastPage = PageCount(bundle) - 1;
                    if (lastPage < 2)
                    {
                        return BadRequest(new { error = "Bundle is too small to damage a data page." });
                    }

                    DamagePage(bundle, lastPage, page => page[IntegrityConstants.PageHeaderSize + 16] ^= 0xFF);
                    break;
                }

                // Both meta slots gone — nothing left to recover identity from. The Unopenable verdict, and the
                // case that most justifies the feature: no engine can open this, so no session-scoped view could
                // ever show it.
                case "unopenable":
                    DamagePage(bundle, 0, page => page.AsSpan(200, 64).Fill(0xAB));
                    DamagePage(bundle, 1, page => page.AsSpan(200, 64).Fill(0xCD));
                    break;

                default:
                    return BadRequest(new { error = $"Unknown variant '{variant}'. Expected meta-slot, checksum or unopenable." });
            }
        }
        catch (IOException ex)
        {
            // Almost always "the database is still open" — worth saying plainly, since the fix is to close it.
            return Conflict(new { error = $"Could not write to the bundle: {ex.Message}" });
        }

        using var source = new OfflineBundlePageSource(bundle);
        var report = IntegrityScanner.Scan(source, new IntegrityOptions { Depth = ScanDepth.Standard });

        return Ok(new DamagedFixtureResponseDto(bundle, variant, report.Verdict.ToString(), report.Findings.Count));
    }

    private static int PageCount(string bundlePath)
    {
        var dataPath = Path.Combine(bundlePath, IntegrityConstants.DataFileName);
        return (int)(new FileInfo(dataPath).Length / IntegrityConstants.PageSize);
    }

    /// <summary>
    /// Read-modify-write of one whole page. Mirrors the engine tests' helper deliberately — the canary should be
    /// damaging the database the same way the unit tests do, so a divergence in what "damaged" means cannot
    /// hide behind two different corruption routines.
    /// </summary>
    private static void DamagePage(string bundlePath, int filePageIndex, Action<byte[]> mutate)
    {
        var dataPath = Path.Combine(bundlePath, IntegrityConstants.DataFileName);
        var page = new byte[IntegrityConstants.PageSize];
        using var fs = new FileStream(dataPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        fs.Seek(filePageIndex * (long)IntegrityConstants.PageSize, SeekOrigin.Begin);
        fs.ReadExactly(page);
        mutate(page);
        fs.Seek(filePageIndex * (long)IntegrityConstants.PageSize, SeekOrigin.Begin);
        fs.Write(page);
    }
}

/// <summary>Request to damage a bundle for an integrity canary.</summary>
/// <param name="Path">Bundle to damage, in place.</param>
/// <param name="Variant">meta-slot (repairable, lossless) · checksum (data loss) · unopenable. Defaults to meta-slot.</param>
public sealed record DamagedFixtureRequestDto(string Path, string Variant = "meta-slot");

/// <summary>The damage that was applied, and what a scan makes of it.</summary>
/// <param name="Path">Resolved bundle directory.</param>
/// <param name="Variant">Variant applied.</param>
/// <param name="Verdict">Verdict a Standard scan now returns — lets the canary assert its precondition.</param>
/// <param name="FindingCount">How many findings the damage produced.</param>
public sealed record DamagedFixtureResponseDto(string Path, string Variant, string Verdict, int FindingCount);
#endif
