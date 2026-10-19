using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// Grep gate for BKP-01: in <c>src/Typhon.Engine/Storage/**</c>, the data file is written by exactly two calls, one inside
/// <c>PagedMMF.IssueDataFileWrite</c> and one inside <c>PagedMMF.IssueDataFileWriteAsync</c>. Everything online backup needs to see every
/// write (the change map, the write gate, copy-before-write) lives in those two methods, so a third write site is a page a backup can miss.
/// </summary>
/// <remarks>
/// Comments and string literals are stripped first (<see cref="StorageDurabilityIdentifierGateTests.StripCommentsAndStringLiterals"/>), so prose
/// that names the API does not count. The same check runs as a step of the merge gate's <c>invariants</c> job.
/// </remarks>
[TestFixture]
public class DataFileWriteChokepointGateTests
{
    private static readonly Regex WriteCall = new(@"\bRandomAccess\s*\.\s*(?:Write|WriteAsync)\s*\(", RegexOptions.Compiled);

    // The nearest member declaration before a call names the method that contains it. Good enough for this folder's layout, and the self-test
    // below proves it can tell a chokepoint from a stray write.
    private static readonly Regex MemberDeclaration =
        new(@"\b(?:private|internal|public|protected)\b[^;{}=]*?\b([A-Za-z_]\w*)\s*(?:<[^>]*>)?\s*\(", RegexOptions.Compiled);

    private static readonly string[] Chokepoints = ["IssueDataFileWrite", "IssueDataFileWriteAsync"];

    [Test]
    public void Storage_HasExactlyTheTwoChokepointWrites()
    {
        var storageDir = StorageDurabilityIdentifierGateTests.LocateStorageDir();
        var sites = new List<(string File, int Line, string Member)>();
        foreach (var file in Directory.EnumerateFiles(storageDir, "*.cs", SearchOption.AllDirectories))
        {
            var stripped = StorageDurabilityIdentifierGateTests.StripCommentsAndStringLiterals(File.ReadAllText(file));
            foreach (var site in FindWriteSites(stripped))
            {
                sites.Add((Path.GetRelativePath(storageDir, file), site.Line, site.Member));
            }
        }

        var report = string.Join("\n  ", sites.ConvertAll(s => $"{s.File}:{s.Line} in {s.Member}"));
        Assert.That(sites, Has.Count.EqualTo(2), $"BKP-01 chokepoint: Storage/ must write the data file at exactly two sites. Found:\n  {report}");
        Assert.That(sites.ConvertAll(s => s.Member), Is.EquivalentTo(Chokepoints),
            $"BKP-01 chokepoint: each write must sit inside IssueDataFileWrite / IssueDataFileWriteAsync. Found:\n  {report}");
    }

    [Test]
    public void Mutant_AThirdWriteSite_IsReported()
    {
        // The matcher's self-test: a write placed in another method must be reported by name, or the gate above proves nothing.
        const string Source = """
            internal class X
            {
                private void IssueDataFileWrite(int filePageIndex, int pageCount, ReadOnlySpan<byte> source)
                {
                    RandomAccess.Write(_fileHandle, source, 0);
                }

                private ValueTask IssueDataFileWriteAsync(int filePageIndex, ReadOnlyMemory<byte> source)
                    => RandomAccess.WriteAsync(_fileHandle, source, 0);

                internal void Sneaky(int page) { RandomAccess.Write(_fileHandle, buffer, page * 8192L); }
            }
            """;

        var sites = FindWriteSites(StorageDurabilityIdentifierGateTests.StripCommentsAndStringLiterals(Source));
        Assert.That(sites.ConvertAll(s => s.Member), Is.EqualTo(new[] { "IssueDataFileWrite", "IssueDataFileWriteAsync", "Sneaky" }));
    }

    private static List<(int Line, string Member)> FindWriteSites(string strippedSource)
    {
        var result = new List<(int, string)>();
        foreach (Match call in WriteCall.Matches(strippedSource))
        {
            string member = "?";
            foreach (Match decl in MemberDeclaration.Matches(strippedSource[..call.Index]))
            {
                member = decl.Groups[1].Value;
            }

            var line = 1;
            for (var i = 0; i < call.Index; i++)
            {
                line += strippedSource[i] == '\n' ? 1 : 0;
            }

            result.Add((line, member));
        }

        return result;
    }
}
