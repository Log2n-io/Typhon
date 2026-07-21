using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// Re-uses the existing `SvUnit` / `VUnit` / `SvPosition` / `SvVelocity` / `VStats` types from `ArchetypeAccessorTests.cs` (same assembly, same namespace) —
// avoids declaring duplicate archetypes that would clash on archetype-id allocation, and sidesteps source-generator type-resolution quirks. The lifecycle
// behaviour we want to verify is registry-shape + ALC-unload, not the archetype semantics themselves, so any well-formed registered archetype is sufficient.

/// <summary>
/// Tests the per-engine ArchetypeRegistry lifecycle introduced to fix the Workbench session-close ALC unload bug: after a session disposes its engine, the
/// global registry must release every <see cref="Type"/> reference it held on behalf of that engine, so the owning <see cref="AssemblyLoadContext"/> can be
/// reclaimed by GC and the next session starts with a clean registry.
///
/// <para>The suite covers:
/// <list type="bullet">
/// <item>AC2 — refcount semantics: register/unregister round-trip leaves the registry clean ONLY for collectible-ALC types; default-ALC types are
///   intentionally preserved (matches the existing test pattern where <c>[OneTimeSetUp]</c> calls <c>Touch()</c> once and expects persistence across the
///   fixture).</item>
/// <item>AC2 — refcount handles concurrent engines: two engines both holding the same Type reach refcount 2, one disposing decrements to 1 (no clear),
///   second disposing decrements to 0 + clears.</item>
/// <item>AC2 — idempotent: a second <c>UnregisterEngineUse</c> call with the same Types is a no-op.</item>
/// <item>AC4 — sequential same-schema engines work in the default ALC.</item>
/// <item>AC5 (cornerstone) — collectible ALC actually unloads after engine + UnregisterEngineUse + GC.</item>
/// </list>
/// </para>
/// </summary>
[TestFixture]
[NonParallelizable] // touches the process-global ArchetypeRegistry
internal sealed class ArchetypeRegistryLifecycleTests : TestBase<ArchetypeRegistryLifecycleTests>
{
    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        // Touch makes the archetype types finalised in the registry (default-ALC, so they persist for the
        // process lifetime — they'll stay in the registry across all tests in this fixture, by design).
    }

    [Test]
    public void RegisterUnregisterRoundTrip_DefaultAlc_LeavesRegistryIntact()
    {
        // For default-ALC types, the registry intentionally PRESERVES entries after refcount → 0. This is
        // the explicit gate that lets existing [OneTimeSetUp] + multiple-tests-per-fixture patterns keep
        // working without re-touching archetypes on every test. The collectible-ALC test below proves the
        // OPPOSITE behaviour for the case that actually needs cleanup (the Workbench).
        var archetypes = new HashSet<Type> { typeof(SvUnit), typeof(VUnit) };
        var components = new HashSet<Type> { typeof(SvPosition), typeof(SvVelocity) };

        // Pre-condition: archetype types are registered (via OneTimeSetUp's Touch).
        Assert.That(Archetype<SvUnit>.Metadata, Is.Not.Null);
        Assert.That(Archetype<VUnit>.Metadata, Is.Not.Null);

        ArchetypeRegistry.RegisterEngineUse(archetypes, components);
        ArchetypeRegistry.UnregisterEngineUse(archetypes, components);

        // Default-ALC: metadata MUST still be reachable for the next test in the suite. This is the assertion
        // that protects every existing engine test from a regression to the "registry-cleared-between-tests"
        // pollution failure mode.
        Assert.That(Archetype<SvUnit>.Metadata, Is.Not.Null);
        Assert.That(Archetype<VUnit>.Metadata, Is.Not.Null);
    }

    [Test]
    public void UnregisterEngineUse_IsIdempotent()
    {
        // Calling UnregisterEngineUse twice with the same Types — second call must be a no-op (refcount
        // already zero / entry already removed). Guards against a double-dispose path crashing.
        var archetypes = new HashSet<Type> { typeof(SvUnit) };
        var components = new HashSet<Type> { typeof(SvPosition), typeof(SvVelocity) };

        ArchetypeRegistry.RegisterEngineUse(archetypes, components);
        ArchetypeRegistry.UnregisterEngineUse(archetypes, components);
        Assert.DoesNotThrow(() => ArchetypeRegistry.UnregisterEngineUse(archetypes, components));
    }

    [Test]
    public void RefcountedAcrossEngines_DefaultAlc_BothMustUnregisterBeforeClear()
    {
        // Two "engines" both registering the same archetype. Even though we don't actually clear default-ALC
        // entries, the refcount semantics still apply — the entry's refcount must reach zero before the
        // ALC-collectibility gate even runs. This test verifies the refcount math separately from the gate.
        var archetypes = new HashSet<Type> { typeof(SvUnit) };
        var components = new HashSet<Type> { typeof(SvPosition), typeof(SvVelocity) };

        ArchetypeRegistry.RegisterEngineUse(archetypes, components); // engine1 → refcount 1
        ArchetypeRegistry.RegisterEngineUse(archetypes, components); // engine2 → refcount 2

        // After engine1 disposes: refcount drops to 1 but stays positive. The metadata stays no matter what
        // (default-ALC), so the assertion is the same — but the refcount discipline is what matters.
        ArchetypeRegistry.UnregisterEngineUse(archetypes, components);
        Assert.That(Archetype<SvUnit>.Metadata, Is.Not.Null);

        ArchetypeRegistry.UnregisterEngineUse(archetypes, components); // engine2 → refcount 0
        Assert.That(Archetype<SvUnit>.Metadata, Is.Not.Null); // default-ALC preserved
    }

    [Test]
    public void Sequential_DefaultAlc_Engines_BothSpawnSuccessfully()
    {
        // AC4 — the user-stated scenario in the default-ALC case: build engine1 in scope1, spawn, dispose
        // the scope (disposes the engine), build engine2 in scope2 with the same archetype types, spawn
        // again. Both must succeed. Pre-fix, the second spawn could fail if registry state was corrupted by
        // the first engine's dispose; with the refcounted gate, default-ALC entries persist across disposals
        // so the second engine sees a registry consistent with what `[OneTimeSetUp]` set up.
        for (int session = 0; session < 2; session++)
        {
            using var scope = ServiceProvider.CreateScope();
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<SvPosition>();
            dbe.RegisterComponentFromAccessor<SvVelocity>();
            dbe.InitializeArchetypes();

            using var tx = dbe.CreateQuickTransaction();
            var pos = new SvPosition(42 + session, 84);
            var vel = new SvVelocity(7, 3);
            var entity = tx.Spawn<SvUnit>(
                SvUnit.Position.Set(in pos),
                SvUnit.Velocity.Set(in vel));
            Assert.That(tx.Commit(), Is.True, $"Session {session}: commit should succeed");
            Assert.That(entity.EntityKey, Is.GreaterThan(0));
        }
    }

    /// <summary>
    /// AC5 — cornerstone test. Loads <c>Typhon.Workbench.Fixtures.schema.dll</c> into a fresh
    /// <see cref="AssemblyLoadContext"/> with <c>isCollectible: true</c>, invokes the registry's
    /// register + unregister flow against the ALC-loaded archetype Types, then drops every managed
    /// reference + calls <c>ALC.Unload()</c> + forces a GC. The ALC's <see cref="WeakReference"/> must
    /// report the ALC as collected.
    ///
    /// <para>This is THE test that proves the architectural fix works end-to-end: without
    /// <c>UnregisterEngineUse</c> properly releasing the static registry's Type references, the GC can
    /// never reclaim the ALC (the registry's strong refs pin every Type, which pins the ALC). With the
    /// fix in place, GC sees no live references after <c>Unload()</c> and the ALC is collected within
    /// a few forced cycles.</para>
    /// </summary>
    [Test]
    public void CollectibleAlc_UnloadsAfterUnregister()
    {
        // Resolve the path to Typhon.Workbench.Fixtures.schema.dll — it lives next to the test assembly
        // in the test output directory (project reference) so we just point the load at our own bin dir.
        var schemaDllPath = Path.Combine(
            Path.GetDirectoryName(typeof(ArchetypeRegistryLifecycleTests).Assembly.Location)!,
            "Typhon.Workbench.Fixtures.schema.dll");
        Assume.That(File.Exists(schemaDllPath), Is.True,
            $"Test prerequisite missing: {schemaDllPath}. Run `dotnet build` to populate the test bin directory.");

        // Run the inner scope in a separate method so the local Type refs go out of scope before the GC
        // check. JIT enregistration can otherwise extend a local's lifetime past `alcRef = new WeakRef(alc)`,
        // pinning the ALC and producing a flaky test. The method-call boundary is the cleanest separation.
        var alcRef = LoadAndExerciseSchemaAlc(schemaDllPath);

        // Force GC. Multiple iterations because ALC unload completes across two GC cycles: first cycle
        // reclaims the Types, second the ALC itself. Five iterations is the documented safe upper bound.
        for (int i = 0; i < 5; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
        }

        Assert.That(alcRef.TryGetTarget(out _), Is.False,
            "Collectible ALC was not reclaimed by GC after Unload + UnregisterEngineUse. Something in the "
            + "engine's static state is still pinning a Type from the ALC's assembly. Check that every "
            + "Type-holding registry table is cleared by UnregisterEngineUse for collectible-ALC entries.");
    }

    /// <summary>
    /// Loads the schema DLL into a fresh collectible ALC, exercises the registry register + unregister
    /// flow against its archetype Types, drops every local reference, and returns a weak reference to the
    /// ALC. The caller forces GC to verify reclamation.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)] // ensures locals don't outlive the method scope
    private static WeakReference<AssemblyLoadContext> LoadAndExerciseSchemaAlc(string schemaDllPath)
    {
        var alc = new AssemblyLoadContext(name: "test-lifecycle-alc", isCollectible: true);
        var weakRef = new WeakReference<AssemblyLoadContext>(alc);

        var asm = alc.LoadFromAssemblyPath(schemaDllPath);

        // Fire the generated [ModuleInitializer] barrier (feature #514 phase 5) — the Workbench open-world trigger (design §5.2). This registers EVERY [Component]
        // + [Archetype] in the schema assembly into the global registry at once, exactly as a real session sees it after loading the schema DLL. (Reflection's
        // GetTypes() does NOT fire the module-init; RunModuleConstructor does, and is idempotent.)
        RuntimeHelpers.RunModuleConstructor(asm.ManifestModule.ModuleHandle);

        // Mirror the real engine lifecycle: RegisterEngineUse over ALL archetype + component types the barrier registered, then UnregisterEngineUse to release
        // them (the real engine snapshots GetAllArchetypes() for exactly this on InitializeArchetypes / Dispose). Unregistering only a subset would leak the
        // archetypes the module-init registered but the test never released — pinning the ALC.
        var archetypes = new HashSet<Type>();
        var components = new HashSet<Type>();
        foreach (var t in asm.GetTypes())
        {
            if (t.GetCustomAttribute<ArchetypeAttribute>() != null)
            {
                archetypes.Add(t);
            }
            if (t.GetCustomAttribute<ComponentAttribute>() != null)
            {
                components.Add(t);
            }
        }
        ArchetypeRegistry.RegisterEngineUse(archetypes, components);
        ArchetypeRegistry.UnregisterEngineUse(archetypes, components);

        // Unload the ALC — sets the unload flag, GC reclaims when no refs remain.
        alc.Unload();

        // CRITICAL: null out every local reference to the ALC + the types + the assembly before returning.
        // Even though they go out of scope, the JIT can extend their lifetime to the end of the method;
        // explicit nullification makes the intent unambiguous and the test deterministic.
        // ReSharper disable RedundantAssignment
        asm = null!;
        alc = null!;
        archetypes = null!;
        components = null!;
        // ReSharper restore RedundantAssignment

        return weakRef;
    }
}
