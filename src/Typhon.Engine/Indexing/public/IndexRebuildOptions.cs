using JetBrains.Annotations;
using System.Collections.Generic;

namespace Typhon.Engine;

/// <summary>
/// How an open rebuilds the secondary indexes it cannot trust or load (a crash open, an added index, a migration): an external merge sort of the indexed
/// keys and an ordered build of each tree, in a fixed memory budget whatever the data's size (#1588).
/// </summary>
[PublicAPI]
public sealed class IndexRebuildOptions
{
    /// <summary>The smallest <see cref="MemoryBudgetBytes"/>: 16 MiB.</summary>
    public const long MinimumMemoryBudgetBytes = 16L << 20;

    /// <summary>
    /// The native memory the rebuild may use, reused by each of its phases: 256 MiB by default, at least 16 MiB. It does not grow with the data: a larger
    /// archetype spills more sorted runs to the temp file instead. Independent of the page cache.
    /// </summary>
    public long MemoryBudgetBytes { get; set; } = 256L << 20;

    /// <summary>
    /// The bytes each sorted run reads from the temp file per refill during the merge: 1 MiB by default, a multiple of 64 KiB. Shrunk, down to 64 KiB, when
    /// many runs share the budget.
    /// </summary>
    public int MergeReadBufferBytes { get; set; } = 1 << 20;

    /// <summary>The temp file's directory; null puts it in the database bundle, <c>{bundle}/rebuild-tmp</c>, on the data file's volume.</summary>
    public string TempDirectory { get; set; }

    internal void Validate(List<string> failures)
    {
        if (MemoryBudgetBytes < MinimumMemoryBudgetBytes)
        {
            failures.Add($"IndexRebuild.MemoryBudgetBytes must be >= {MinimumMemoryBudgetBytes} (was {MemoryBudgetBytes}).");
        }

        if (MergeReadBufferBytes < 64 * 1024 || MergeReadBufferBytes % (64 * 1024) != 0)
        {
            failures.Add($"IndexRebuild.MergeReadBufferBytes must be a non-zero multiple of 65536 (was {MergeReadBufferBytes}).");
        }
    }
}
