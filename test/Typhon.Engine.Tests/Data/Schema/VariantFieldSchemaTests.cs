using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[TestFixture]
class VariantFieldSchemaTests
{
    [Component("Typhon.Test.Variant.Generated", 1, StorageMode = StorageMode.SingleVersion)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct GeneratedVariant
    {
        public int Before;
        public Variant Value;
        public int After;

        public GeneratedVariant(int before, Variant value, int after) => (Before, Value, After) = (before, value, after);
    }

    // A private type cannot be reached by the generated registrar, so this exercises runtime reflection.
    [Component("Typhon.Test.Variant.Reflected", 1, StorageMode = StorageMode.SingleVersion)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ReflectedVariant
    {
        public int Before;
        public Variant Value;
        public int After;

        public ReflectedVariant(int before, Variant value, int after) => (Before, Value, After) = (before, value, after);
    }

    [Component("Typhon.Test.Variant.Only", 1)]
    private struct OnlyVariant
    {
        public Variant Value;

        public OnlyVariant(Variant value) => Value = value;
    }

    [Component("Typhon.Test.Variant.Indexed", 1)]
    private struct IndexedVariant
    {
        [Index] public Variant Value;

        public IndexedVariant(Variant value) => Value = value;
    }

    [Test]
    public void VariantMapsToItsOwn64ByteFieldType()
    {
        Assert.Multiple(() =>
        {
            Assert.That(DatabaseSchemaExtensions.FromType<Variant>(), Is.EqualTo((FieldType.Variant, FieldType.None)));
            Assert.That(DatabaseSchemaExtensions.FromType(typeof(Variant)), Is.EqualTo((FieldType.Variant, FieldType.None)));
            Assert.That(FieldType.Variant.FieldSizeInComp(), Is.EqualTo(64));
            Assert.That(Unsafe.SizeOf<Variant>(), Is.EqualTo(64));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void VariantFieldsHaveIdsAndCorrectOffsets(bool generated)
    {
        var type = generated ? typeof(GeneratedVariant) : typeof(ReflectedVariant);
        Assert.That(GeneratedSchemaRegistry.TryGetComponentSpec(type, out _), Is.EqualTo(generated), "exercise both schema build paths");

        var definition = new DatabaseDefinitions().CreateFromAccessor(type);
        var value = definition.FieldsByName["Value"];

        Assert.Multiple(() =>
        {
            Assert.That(definition.FieldsByName.Count, Is.EqualTo(3));
            Assert.That(definition.GetFieldId("Before"), Is.EqualTo(0));
            Assert.That(value.FieldId, Is.EqualTo(1));
            Assert.That(definition.GetFieldId("After"), Is.EqualTo(2));
            Assert.That(value.Type, Is.EqualTo(FieldType.Variant));
            Assert.That(value.DotNetType, Is.EqualTo(typeof(Variant)));
            Assert.That(value.FieldSize, Is.EqualTo(64));
            Assert.That(value.SizeInComponentStorage, Is.EqualTo(64));
            Assert.That(value.OffsetInComponentStorage, Is.EqualTo(4));
            Assert.That(definition.FieldsByName["After"].OffsetInComponentStorage, Is.EqualTo(68));
            Assert.That(definition.ComponentStorageSize, Is.EqualTo(72));
        });
    }

    [Test]
    public void AVariantCanBeTheOnlyField()
    {
        var definition = new DatabaseDefinitions().CreateFromAccessor<OnlyVariant>();

        Assert.Multiple(() =>
        {
            Assert.That(definition.FieldsByName.Count, Is.EqualTo(1));
            Assert.That(definition.GetFieldId("Value"), Is.EqualTo(0));
            Assert.That(definition.FieldsByName["Value"].Type, Is.EqualTo(FieldType.Variant));
            Assert.That(definition.FieldsByName["Value"].SizeInComponentStorage, Is.EqualTo(64));
            Assert.That(definition.ComponentStorageSize, Is.EqualTo(64));
        });
    }

    [Test]
    public void AVariantIndexIsRefusedAtRegistration()
    {
        var error = Assert.Throws<Exception>(() => new DatabaseDefinitions().CreateFromAccessor<IndexedVariant>());

        Assert.That(error.Message, Does.Contain("Variant").And.Contain("Value").And.Contain("does not support index"));
    }
}
