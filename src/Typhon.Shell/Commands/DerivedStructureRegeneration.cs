using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using Typhon.Engine;

namespace Typhon.Shell.Commands;

/// <summary>
/// Opens a bundle so the engine's own rebuild net regenerates its derived structures, then closes it cleanly.
/// </summary>
/// <remarks>
/// <para>
/// This is the callback <see cref="DatabaseRepair.Apply"/> asks for and, until now, nothing supplied — so a plan
/// containing a <c>RegenerateDerivedStructures</c> step threw when applied. That is worse than the step being
/// unimplemented: the plan is shown to an operator as a list of what will happen, and they consented to a step the
/// tool could not perform.
/// </para>
/// <para>
/// <b>Repair by opening is deliberate, not lazy.</b> Indexes, entity maps, revision chains, cluster heads and spatial
/// state are pure functions of primary data, and the engine already rebuilds them in the order the rules require —
/// chains scrubbed before indexes are built over them, the entity map re-derived before anything reads it. Reproducing
/// that ordering inside the repair module would be a second implementation of the same invariants, drifting from the
/// first. <c>05 §1</c> makes the same argument: most of repair already exists.
/// </para>
/// <para>
/// <b>The engine is given no schema, and that is the interesting part.</b> Nothing here registers a component type, so
/// the rebuild runs entirely off what the file describes about itself. If regeneration needed the schema assembly, this
/// would fail on exactly the forensic case the feature exists for — a database being recovered on a machine that never
/// ran the application.
/// </para>
/// </remarks>
internal static class DerivedStructureRegeneration
{
    /// <summary>
    /// Opens the bundle, lets the rebuild net run, and closes cleanly.
    /// </summary>
    /// <param name="bundlePath">Path to the <c>.typhon</c> bundle.</param>
    /// <exception cref="InvalidOperationException">The database could not be opened or closed cleanly.</exception>
    public static void Run(string bundlePath)
    {
        ArgumentNullException.ThrowIfNull(bundlePath);

        var resolved = Path.GetFullPath(bundlePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var directory = Path.GetDirectoryName(resolved);
        var name = Path.GetFileNameWithoutExtension(resolved);

        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(name))
        {
            throw new InvalidOperationException($"'{bundlePath}' does not name a database bundle.");
        }

        var services = new ServiceCollection();
        services
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Error))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(opts =>
            {
                opts.DatabaseName = name;
                opts.DatabaseDirectory = directory;
            })
            .AddScopedDatabaseEngine(opts => opts.Wal = new WalWriterOptions { UseFUA = false });

        using var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var engine = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();

            // Opening is what triggers the rebuild net; the checkpoint is what makes its output durable. Without the
            // checkpoint the regeneration lives only in the page cache and the close could leave the file exactly as it
            // was found — a repair that reports success and changes nothing.
            engine.InitializeArchetypes();
            engine.ForceCheckpoint();
        }

        // Disposing the provider closes the engine, and the close is what stamps the clean-shutdown flag. A repair that
        // regenerated correctly but exited without it would leave a database that reads as crash-path, and half the
        // checks stand down on those.
    }
}
