using NUnit.Framework;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Tests;

[TestFixture]
public class ViewDeltaEntryTests
{
    #region KeyBytes8 Size

    [Test]
    public void KeyBytes8_Size_Is8Bytes() =>
        Assert.That(Unsafe.SizeOf<KeyBytes8>(), Is.EqualTo(8));

    #endregion

    #region ViewDeltaEntry Size

    [Test]
    public void ViewDeltaEntry_Size_Is24Bytes() =>
        Assert.That(Unsafe.SizeOf<ViewDeltaEntry>(), Is.EqualTo(24));

    #endregion

    #region ViewDeltaEntry Field Offsets

    [Test]
    public void FieldOffset_EntityPK_Is0() =>
        Assert.That(Marshal.OffsetOf<ViewDeltaEntry>(nameof(ViewDeltaEntry.EntityPK)).ToInt32(), Is.EqualTo(0));

    [Test]
    public void FieldOffset_BeforeKey_Is8() =>
        Assert.That(Marshal.OffsetOf<ViewDeltaEntry>(nameof(ViewDeltaEntry.BeforeKey)).ToInt32(), Is.EqualTo(8));

    [Test]
    public void FieldOffset_AfterKey_Is16() =>
        Assert.That(Marshal.OffsetOf<ViewDeltaEntry>(nameof(ViewDeltaEntry.AfterKey)).ToInt32(), Is.EqualTo(16));

    #endregion

    #region ViewDeltaEntry Defaults

    [Test]
    public void Default_HasZeroEntityPKAndKeys()
    {
        var entry = new ViewDeltaEntry();
        Assert.That(entry.EntityPK, Is.EqualTo(0));
        Assert.That(entry.BeforeKey.IsZero, Is.True);
        Assert.That(entry.AfterKey.IsZero, Is.True);
    }

    #endregion

    #region KeyBytes8 IsZero

    [Test]
    public void KeyBytes8_IsZero_DefaultTrueNonZeroFalse()
    {
        Assert.That(new KeyBytes8().IsZero, Is.True);
        Assert.That(KeyBytes8.FromInt(0).IsZero, Is.True);
        Assert.That(KeyBytes8.FromInt(42).IsZero, Is.False);
    }

    #endregion

    #region KeyBytes8 Roundtrip — One per type

    [Test]
    public void KeyBytes8_Roundtrip_Int()
    {
        var kb = KeyBytes8.FromInt(12345);
        Assert.That(kb.AsInt(), Is.EqualTo(12345));
    }

    [Test]
    public void KeyBytes8_Roundtrip_Long()
    {
        var kb = KeyBytes8.FromLong(9876543210L);
        Assert.That(kb.AsLong(), Is.EqualTo(9876543210L));
    }

    [Test]
    public void KeyBytes8_Roundtrip_Float()
    {
        var kb = KeyBytes8.FromFloat(3.14f);
        Assert.That(kb.AsFloat(), Is.EqualTo(3.14f));
    }

    [Test]
    public void KeyBytes8_Roundtrip_Double()
    {
        var kb = KeyBytes8.FromDouble(3.141592653589793);
        Assert.That(kb.AsDouble(), Is.EqualTo(3.141592653589793));
    }

    [Test]
    public void KeyBytes8_Roundtrip_Bool()
    {
        Assert.That(KeyBytes8.FromBool(true).AsLong(), Is.EqualTo(1));
        Assert.That(KeyBytes8.FromBool(false).AsLong(), Is.EqualTo(0));
    }

    [Test]
    public void KeyBytes8_Roundtrip_Byte()
    {
        var kb = KeyBytes8.FromByte(255);
        Assert.That(kb.AsInt(), Is.EqualTo(255));
    }

    [Test]
    public void KeyBytes8_Roundtrip_SByte()
    {
        var kb = KeyBytes8.FromSByte(127);
        Assert.That(kb.AsInt(), Is.EqualTo(127));
    }

    [Test]
    public void KeyBytes8_Roundtrip_Short()
    {
        var kb = KeyBytes8.FromShort(32000);
        Assert.That(kb.AsInt(), Is.EqualTo(32000));
    }

    [Test]
    public void KeyBytes8_Roundtrip_UShort()
    {
        var kb = KeyBytes8.FromUShort(65535);
        Assert.That(kb.AsInt(), Is.EqualTo(65535));
    }

    [Test]
    public void KeyBytes8_Roundtrip_UInt()
    {
        var kb = KeyBytes8.FromUInt(uint.MaxValue);
        Assert.That(kb.AsLong(), Is.EqualTo((long)uint.MaxValue));
    }

    [Test]
    public void KeyBytes8_Roundtrip_ULong()
    {
        var kb = KeyBytes8.FromULong(12345678901234UL);
        Assert.That((ulong)kb.AsLong(), Is.EqualTo(12345678901234UL));
    }

    #endregion

    #region KeyBytes8 FromRef

    [Test]
    public unsafe void KeyBytes8_FromRef_ReadsCorrectBytes()
    {
        long source = 0x0102030405060708L;
        byte* ptr = (byte*)&source;

        var kb = KeyBytes8.FromRef(ref *ptr, 8);
        Assert.That(kb.AsLong(), Is.EqualTo(0x0102030405060708L));
    }

    #endregion

    #region KeyBytes8 RawValue

    [Test]
    public void KeyBytes8_RawValue_GetSet()
    {
        var kb = new KeyBytes8();
        kb.RawValue = 0x7FFFFFFFFFFFFFFF;
        Assert.That(kb.RawValue, Is.EqualTo(0x7FFFFFFFFFFFFFFF));
    }

    [Test]
    public void KeyBytes8_RawValue_MatchesAsLong()
    {
        var kb = KeyBytes8.FromLong(42);
        Assert.That(kb.RawValue, Is.EqualTo(kb.AsLong()));
    }

    #endregion
}
