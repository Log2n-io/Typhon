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
/// Validates the <b>wired</b> knobs of <see cref="DatabaseEngineOptions.Resources"/> at DI options-resolution time. Only the
/// fields that actually drive engine behavior are range-checked: <see cref="ResourceOptions.MaxActiveTransactions"/> (the
/// transaction-chain cap), <see cref="ResourceOptions.WalRingBufferSizeBytes"/> (commit-buffer capacity), and the two
/// checkpoint timers. The remaining <see cref="ResourceOptions"/> fields are vestigial — referenced only by the never-called
/// budget <see cref="ResourceOptions.Validate"/> — and are deliberately NOT validated, to avoid guarding knobs that govern
/// nothing (the same reason <c>MemoryAllocatorOptions</c> / <c>ResourceRegistryOptions</c>, which carry only a diagnostic
/// Name, get no validator at all). See issue #148.
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

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
