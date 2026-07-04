using JetBrains.Annotations;
using Microsoft.Extensions.Options;
using System.Collections.Generic;

namespace Typhon.Engine.internals;

/// <summary>
/// Validates <see cref="PagedMMFOptions"/> (and derived option types such as <c>ManagedPagedMMFOptions</c>) at DI
/// options-resolution time by delegating to the type's own <see cref="PagedMMFOptions.Validate(bool, out string)"/> — the
/// single source of truth for storage-config rules (database name, directory, cache size). Surfacing the failure here makes a
/// startup misconfiguration fail fast with the specific rule message, before the engine constructs the file (which would
/// otherwise throw the same rules later as an <see cref="System.ArgumentException"/>). See issue #148.
/// </summary>
[UsedImplicitly]
internal sealed class PagedMMFOptionsValidator<TO> : IValidateOptions<TO> where TO : PagedMMFOptions
{
    public ValidateOptionsResult Validate(string name, TO options) =>
        options.Validate(silent: true, out var message) ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(message);
}

/// <summary>
/// Validates the <b>wired</b> knobs of <see cref="DatabaseEngineOptions"/> at DI options-resolution time: the
/// <see cref="ResourceOptions"/> settings (<see cref="ResourceOptions.MaxActiveTransactions"/>,
/// <see cref="ResourceOptions.WalRingBufferSizeBytes"/>, and the two checkpoint timers) and, when present, the
/// <see cref="WalWriterOptions"/> knobs (segment / staging-buffer sizes, group-commit interval, pre-allocation). Only
/// fields that actually drive engine behavior are checked — the aspirational memory-budget knobs that used to live on
/// <see cref="ResourceOptions"/> were removed as vestigial in #148 (the same reason <c>MemoryAllocatorOptions</c> /
/// <c>ResourceRegistryOptions</c>, which carry only a diagnostic Name, get no validator at all). See issue #148.
/// </summary>
[UsedImplicitly]
internal sealed class DatabaseEngineOptionsValidator : IValidateOptions<DatabaseEngineOptions>
{
    public ValidateOptionsResult Validate(string name, DatabaseEngineOptions options)
    {
        var resources = options.Resources;
        if (resources == null)
        {
            return ValidateOptionsResult.Fail("DatabaseEngineOptions.Resources must not be null.");
        }

        var failures = new List<string>();

        if (resources.MaxActiveTransactions <= 0)
        {
            failures.Add($"Resources.MaxActiveTransactions must be > 0 (was {resources.MaxActiveTransactions}).");
        }

        if (resources.WalRingBufferSizeBytes <= 0)
        {
            failures.Add($"Resources.WalRingBufferSizeBytes must be > 0 (was {resources.WalRingBufferSizeBytes}).");
        }

        if (resources.CheckpointIntervalMs <= 0)
        {
            failures.Add($"Resources.CheckpointIntervalMs must be > 0 (was {resources.CheckpointIntervalMs}).");
        }

        if (resources.CheckpointBarrierTimeoutMs <= 0)
        {
            failures.Add($"Resources.CheckpointBarrierTimeoutMs must be > 0 (was {resources.CheckpointBarrierTimeoutMs}).");
        }

        // WalWriterOptions is the real WAL config (unlike the removed vestigial ResourceOptions budget knobs). Validate its
        // wired invariants when present; the engine tolerates a null Wal by deriving defaults, so a null is not an error here.
        var wal = options.Wal;
        if (wal != null)
        {
            if (wal.SegmentSize == 0)
            {
                failures.Add($"Wal.SegmentSize must be > 0 (was {wal.SegmentSize}).");
            }

            // Documented constraint: the O_DIRECT staging buffer must be a positive multiple of 4096.
            if (wal.StagingBufferSize <= 0 || (wal.StagingBufferSize % 4096) != 0)
            {
                failures.Add($"Wal.StagingBufferSize must be a positive multiple of 4096 (was {wal.StagingBufferSize}).");
            }

            if (wal.GroupCommitIntervalMs <= 0)
            {
                failures.Add($"Wal.GroupCommitIntervalMs must be > 0 (was {wal.GroupCommitIntervalMs}).");
            }

            if (wal.PreAllocateSegments < 0)
            {
                failures.Add($"Wal.PreAllocateSegments must be >= 0 (was {wal.PreAllocateSegments}).");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
