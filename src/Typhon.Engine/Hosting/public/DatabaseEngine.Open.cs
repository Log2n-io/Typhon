using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;

namespace Typhon.Engine;

public partial class DatabaseEngine
{
    // Set only by Open(): the private service container this engine owns and must dispose. Null on the DI path, where the
    // host owns the container. Disposed in Dispose(bool) — see the re-entrancy note there.
    private ServiceProvider _ownedProvider;

    /// <summary>
    /// Opens (or creates) a database backed by a self-contained service container — the standalone counterpart to the DI
    /// <see cref="ServiceCollectionExtensions.AddTyphon"/> path. The returned engine <b>owns</b> the container and disposes
    /// it as part of its own disposal, so <c>using var dbe = DatabaseEngine.Open(...)</c> is leak-free.
    /// </summary>
    /// <param name="databaseFile">The database path; the canonical extension is <c>.typhon</c>. See <see cref="TyphonOptions.DatabaseFile"/>.</param>
    /// <param name="configure">Optional further configuration — component/archetype registrations and advanced options.</param>
    /// <param name="loggerFactory">Optional logger factory. When <see langword="null"/>, logging resolves to a no-op (no output)
    /// while still satisfying the engine's required loggers.</param>
    /// <returns>A ready-to-use engine that owns and disposes its private container.</returns>
    public static DatabaseEngine Open(string databaseFile, Action<TyphonOptions> configure = null, ILoggerFactory loggerFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseFile);

        var services = new ServiceCollection();
        if (loggerFactory != null)
        {
            // Register the caller's factory first: AddLogging uses TryAdd and will not override it, so ILogger<T> resolves
            // through the supplied factory.
            services.AddSingleton(loggerFactory);
        }

        services.AddLogging();

        services.AddTyphon(o =>
        {
            o.DatabaseFile(databaseFile);
            configure?.Invoke(o);
        });

        var provider = services.BuildServiceProvider();
        try
        {
            var engine = provider.GetRequiredService<DatabaseEngine>();
            engine.AttachOwnedProvider(provider);
            return engine;
        }
        catch
        {
            // Opening failed after the container was built (bad config, locked file, init error) — dispose the private
            // container so its file handles / threads don't leak, then surface the original error.
            provider.Dispose();
            throw;
        }
    }

    // Adopts the private container built by Open(). Disposed inside Dispose(bool).
    private void AttachOwnedProvider(ServiceProvider provider) => _ownedProvider = provider;
}
