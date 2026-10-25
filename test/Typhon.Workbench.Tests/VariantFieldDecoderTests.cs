using System.Runtime.InteropServices;
using NUnit.Framework;
using Typhon.Engine;
using Typhon.Schema.Definition;
using Typhon.Workbench.DataBrowser;

namespace Typhon.Workbench.Tests;

[TestFixture]
public sealed class VariantFieldDecoderTests
{
    [Component("Typhon.Test.Workbench.Variant", 1)]
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct VariantComponent
    {
        public int Before;
        public Variant Value;
        public int After;
    }

    [Test]
    public void ARegisteredVariantFieldDecodesItsTaggedText()
    {
        var definition = new DatabaseDefinitions().CreateFromAccessor<VariantComponent>();
        VariantComponent[] components = [new() { Before = 101, Value = new Variant(42), After = 202 }];
        var field = definition.FieldsByName["Value"];

        var decoded = ComponentValueDecoder.Decode(field, MemoryMarshal.AsBytes(components.AsSpan()));

        Assert.Multiple(() =>
        {
            Assert.That(decoded.FieldId, Is.EqualTo(1));
            Assert.That(decoded.Value, Is.EqualTo("si:42"));
            Assert.That(decoded.Raw, Does.StartWith("73693a343200"));
            Assert.That(decoded.Raw.Length, Is.EqualTo(128), "the raw representation includes all 64 bytes");
        });
    }
}
