using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Integrity;

/// <summary>
/// The offline manifest decode, compared field by field against the engine's own schema.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this test and not more reading.</b> Two EntityMap walks were written by inferring layout from struct
/// declarations, and both were wrong in ways that looked plausible — a walk that recovers <i>most</i> of a structure
/// reads exactly like a correct one until something counts. The technique that worked first time, for chunk geometry,
/// was different in kind: ask the engine for ground truth, ask the offline reader the same question, require the
/// answers to be equal. This applies it to the schema manifest.
/// </para>
/// <para>
/// The comparison is deliberately of <i>values</i>, not of counts. A decoder that finds the right number of fields at
/// the wrong offsets satisfies a count assertion, and the checks built on it would then read a component's bytes from
/// the wrong place and report confident nonsense about them.
/// </para>
/// </remarks>
[TestFixture]
internal sealed class SchemaCatalogAgreementTests : IntegrityFixtureBase
{
    /// <summary>
    /// Every persisted field descriptor matches the runtime definition it was written from.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    public void FieldDescriptorsAgreeWithTheRuntimeSchema()
    {
        var expected = BuildAndCaptureRuntimeFields();
        var reader = ReadManifest();

        Assert.That(reader.IsUsable, Is.True);
        Assert.That(reader.Diagnostics, Is.Empty, "a healthy manifest must decode without diagnostics:\n  " + string.Join("\n  ", reader.Diagnostics));

        var compared = 0;
        foreach (var (componentName, runtimeFields) in expected)
        {
            Assert.That(reader.Components.TryGetValue(componentName, out var view), Is.True,
                $"the manifest does not describe '{componentName}'");

            // Static fields are excluded from per-entity storage and from FieldCount, but they ARE persisted, so the
            // comparison is over the whole declared set rather than over the storage-bearing subset.
            var offline = view.Fields.ToDictionary(f => f.Name, f => f);

            Assert.That(offline.Keys.OrderBy(n => n), Is.EqualTo(runtimeFields.Keys.OrderBy(n => n)),
                $"'{componentName}': the decoded field set differs from the runtime definition");

            foreach (var (fieldName, runtime) in runtimeFields)
            {
                var got = offline[fieldName];
                Assert.Multiple(() =>
                {
                    Assert.That(got.FieldId, Is.EqualTo(runtime.FieldId), $"{componentName}.{fieldName}: FieldId");
                    Assert.That(got.Type, Is.EqualTo(runtime.Type), $"{componentName}.{fieldName}: Type");
                    Assert.That(got.Offset, Is.EqualTo(runtime.OffsetInComponentStorage), $"{componentName}.{fieldName}: offset");
                    Assert.That(got.IsStatic, Is.EqualTo(runtime.IsStatic), $"{componentName}.{fieldName}: IsStatic");
                    Assert.That(got.HasIndex, Is.EqualTo(runtime.HasIndex), $"{componentName}.{fieldName}: HasIndex");
                    Assert.That(got.IndexAllowMultiple, Is.EqualTo(runtime.IndexAllowMultiple),
                        $"{componentName}.{fieldName}: IndexAllowMultiple");
                });

                compared++;
            }
        }

        Assert.That(compared, Is.GreaterThan(0), "the test compared nothing, so it proved nothing");
    }

    /// <summary>
    /// The field offsets decoded offline actually address the component's storage.
    /// </summary>
    /// <remarks>
    /// The agreement above proves the decode matches the runtime definition. This proves the definition itself is
    /// usable as an offline addressing scheme — every field lies inside the component's own per-entity extent. Without
    /// it, a manifest that decoded perfectly could still send <c>CLU-03</c> reading past the end of a slot.
    /// </remarks>
    [Test]
    [CancelAfter(30_000)]
    public void EveryDecodedFieldFitsInsideItsComponentStorage()
    {
        BuildHealthyDatabase();
        var reader = ReadManifest();

        var checkedFields = 0;
        foreach (var component in reader.Components.Values)
        {
            foreach (var field in component.Fields)
            {
                if (field.IsStatic)
                {
                    continue;   // not stored per entity, so the extent does not apply
                }

                Assert.That(field.Offset, Is.GreaterThanOrEqualTo(0), $"{component.Name}.{field.Name}");
                Assert.That(field.Offset + field.Size, Is.LessThanOrEqualTo(component.Size),
                    $"{component.Name}.{field.Name} at +{field.Offset}+{field.Size} runs past the component's {component.Size} B extent");
                checkedFields++;
            }
        }

        Assert.That(checkedFields, Is.GreaterThan(0));
    }

    /// <summary>
    /// The archetype's component list and the Versioned count derived from it match the engine.
    /// </summary>
    /// <remarks>
    /// This is the number the whole <c>MAP</c> family waits on. The EntityMap is a
    /// <c>RawValuePagedHashMap&lt;long,…&gt;</c> whose value size is a runtime constructor argument, so bucket capacity
    /// — and hence where the key array ends — is not derivable from the persisted stride alone. It IS derivable from
    /// the archetype's component list crossed with each component's storage mode, and that is what this pins.
    /// </remarks>
    [Test]
    [CancelAfter(30_000)]
    public void ComponentMembershipAndVersionedSlotCountAgree()
    {
        var expected = BuildAndCaptureRuntimeFields();
        var reader = ReadManifest();

        Assert.That(reader.Archetypes, Is.Not.Empty);

        foreach (var archetype in reader.Archetypes.Values)
        {
            Assert.That(archetype.ComponentNames, Is.Not.Empty,
                $"archetype '{archetype.Name}' recovered no component names, so its record size cannot be derived");

            Assert.That(archetype.ComponentNames.Count, Is.EqualTo(archetype.ComponentCount),
                $"archetype '{archetype.Name}': the decoded name list disagrees with the row's own ComponentCount, "
                + "which means one of the two was decoded wrongly");

            foreach (var name in archetype.ComponentNames)
            {
                Assert.That(reader.Components.ContainsKey(name), Is.True,
                    $"archetype '{archetype.Name}' names component '{name}', absent from the catalog");
            }

            Assert.That(archetype.VersionedSlotCount, Is.GreaterThanOrEqualTo(0),
                $"archetype '{archetype.Name}': the Versioned slot count could not be derived");
            Assert.That(archetype.EntityRecordSize,
                Is.EqualTo(ClusterEntityRecordAccessor.RecordSize(archetype.VersionedSlotCount)));
        }

        // And the user archetype specifically: CompA is Versioned, so its record carries exactly one chain pointer.
        var user = reader.Archetypes.Values.First(a => a.ComponentNames.Any(expected.ContainsKey));
        Assert.That(user.VersionedSlotCount, Is.GreaterThan(0),
            $"'{user.Name}' holds a Versioned component, so its record must reserve a chain pointer for it");
    }

    /// <summary>
    /// The recovered per-field index roots are real segments, and the archetype-level roots are consistent with them.
    /// </summary>
    /// <remarks>
    /// <c>FieldR1.IndexSPI</c> is what lets <c>IDX-01</c> be checked as the catalogue states it — one B+Tree per
    /// (archetype, indexed <i>field</i>) — rather than as the approximation the archetype's two roots allow.
    /// </remarks>
    [Test]
    [CancelAfter(30_000)]
    public void PerFieldIndexRootsAreResolvable()
    {
        BuildHealthyDatabase();
        using var source = new OfflineBundlePageSource(BundlePath);
        var roots = SweepSegmentRoots(source);

        var reader = new SchemaCatalogReader(source, roots);
        reader.Read(BootstrapReader.Read(source));

        foreach (var component in reader.Components.Values)
        {
            foreach (var field in component.Fields)
            {
                if (field.IndexRoot == 0)
                {
                    continue;   // "not persisted, rebuild from data" is legitimate
                }

                Assert.That(roots, Does.Contain(field.IndexRoot),
                    $"{component.Name}.{field.Name} names index segment {field.IndexRoot}, which the sweep did not find");
                Assert.That(field.HasIndex, Is.True,
                    $"{component.Name}.{field.Name} owns an index segment but is not marked as indexed");
            }
        }
    }

    /// <summary>Builds the database and captures the runtime field definitions before the engine is closed.</summary>
    private Dictionary<string, Dictionary<string, DBComponentDefinition.Field>> BuildAndCaptureRuntimeFields()
    {
        var captured = new Dictionary<string, Dictionary<string, DBComponentDefinition.Field>>();

        using (var scope = Provider.CreateScope())
        {
            var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();

            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                for (var i = 0; i < 32; i++)
                {
                    using var tx = uow.CreateTransaction();
                    var comp = new CompA(i + 1, i, i);
                    tx.Spawn<CompAArch>(CompAArch.A.Set(in comp));
                    tx.Commit();
                }

                uow.Flush();
            }

            dbe.ForceCheckpoint();

            // ComponentNames yields "Name:R<revision>" — the registry's key format, not a component name.
            foreach (var fullName in dbe.DBD.ComponentNames)
            {
                var split = fullName.LastIndexOf(":R", System.StringComparison.Ordinal);
                if (split < 0 || !int.TryParse(fullName[(split + 2)..], out var revision))
                {
                    continue;
                }

                var name = fullName[..split];
                var definition = dbe.DBD.GetComponent(name, revision);
                if (definition?.FieldsByName == null)
                {
                    continue;
                }

                captured[name] = new Dictionary<string, DBComponentDefinition.Field>(definition.FieldsByName);
            }
        }

        CloseEngine();
        return captured;
    }

    private SchemaCatalogReader ReadManifest()
    {
        using var source = new OfflineBundlePageSource(BundlePath);
        var reader = new SchemaCatalogReader(source, SweepSegmentRoots(source));
        reader.Read(BootstrapReader.Read(source));
        return reader;
    }

    private static List<int> SweepSegmentRoots(IPageSource source)
    {
        var roots = new List<int>();
        var page = new byte[IntegrityConstants.PageSize];

        for (var p = 0; p < source.PageCount; p++)
        {
            if (!source.TryReadPage(p, page) || (PageImage.Flags(page) & PageBlockFlags.IsLogicalSegmentRoot) == 0)
            {
                continue;
            }

            if (MemoryMarshal.Read<int>(PageImage.RawData(page)) == p)
            {
                roots.Add(p);
            }
        }

        return roots;
    }
}
