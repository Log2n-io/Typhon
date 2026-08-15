using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

public class DatabaseSchemaTests
{
    private IServiceProvider _serviceProvider;
    private ServiceCollection _serviceCollection;

    private string CurrentDatabaseName => $"{TestContext.CurrentContext.Test.Name}_database";

    [SetUp]
    public void Setup()
    {
        var o = TestContext.CurrentContext.Test.Properties.ContainsKey("CacheSize");
        var dcs = o ? (int)TestContext.CurrentContext.Test.Properties.Get("CacheSize")! : (int)PagedMMF.MinimumCacheSize;

        var serviceCollection = new ServiceCollection();
        _serviceCollection = serviceCollection;
        _serviceCollection
            .AddLogging(builder =>
            {
                builder.AddSimpleConsole(options =>
                {
                    options.SingleLine = true;
                    options.IncludeScopes = true;
                    options.TimestampFormat = "mm:ss.fff ";
                });
                builder.SetMinimumLevel(LogLevel.Information);
            })
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(options =>
            {
                options.DatabaseName = CurrentDatabaseName;
                options.DatabaseCacheSize = (ulong)dcs;
                options.PagesDebugPattern = false;
            })
            .AddScopedDatabaseEngine(options =>
            {
            });
        
        _serviceProvider = _serviceCollection.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown() => (_serviceProvider as IDisposable)?.Dispose();

    public struct DBObject
    {
        
    }
    [Test]
    public void TestDatabaseSchema()
    {
        var dc = new DatabaseDefinitions();

        dc.CreateFromAccessor<ComponentR1>();

        // No WithPOCO: the fields below are declared by hand and do not correspond to DBObject's own layout, so naming it as the backing struct would assert a
        // stride they do not fit (#816, rule SCHEMA-06). A definition with no backing type is metadata-only, which is exactly what this builder produces here.
        dc.CreateComponentBuilder("DBObject", 1)
            .WithField<String64>(-1, "DBObjectTypeName", 0).IsStatic()
            .WithField<long>(0, "ID", 0)
            .Build();
    }

    [Test]
    unsafe public void TestSchemaStore()
    {

        //Assert.That(_dbe.IsInitialized, Is.True);

    }
}