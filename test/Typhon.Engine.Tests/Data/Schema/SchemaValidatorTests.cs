using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// Pure unit tests for <see cref="SchemaValidator"/> — no database needed.
/// Tests the diff algorithm: type changes, offsets, widenings, additions, removals.
/// </summary>
class SchemaValidatorTests
{
    private static FieldR1 MakeField(string name, int fieldId, FieldType type, int offset = 0, int size = 0,
        bool hasIndex = false, bool indexAllowMultiple = false, int arrayLength = 0) =>
        new()
        {
            Name = (String64)name,
            FieldId = fieldId,
            Type = type,
            OffsetInComponentStorage = offset,
            SizeInComponentStorage = size,
            HasIndex = hasIndex,
            IndexAllowMultiple = indexAllowMultiple,
            ArrayLength = arrayLength,
        };

    private static ComponentR1 MakeComponent(int schemaRevision = 0, int fieldCount = 0) =>
        new()
        {
            Name = (String64)"Test",
            SchemaRevision = schemaRevision,
            FieldCount = fieldCount,
        };

    /// <summary>
    /// Creates a minimal DBComponentDefinition with the specified fields (no real struct needed).
    /// </summary>
    private static DBComponentDefinition MakeDefinition(params (int FieldId, string Name, FieldType Type, int Offset, int Size, bool HasIndex, bool IndexAllowMultiple, int ArrayLength)[] fields)
    {
        var def = new DBComponentDefinition("Test", 1) { POCOType = typeof(int) };
        foreach (var (fieldId, name, type, offset, size, hasIndex, indexAllowMultiple, arrayLength) in fields)
        {
            var f = def.CreateField(fieldId, name, type, FieldType.None, offset, typeof(int));
            f.HasIndex = hasIndex;
            f.IndexAllowMultiple = indexAllowMultiple;
            f.ArrayLength = arrayLength;
        }

        def.Build();
        return def;
    }

    private static DBComponentDefinition MakeSimpleDefinition(params (int FieldId, string Name, FieldType Type, int Offset, int Size)[] fields)
    {
        var tuples = new (int, string, FieldType, int, int, bool, bool, int)[fields.Length];
        for (var i = 0; i < fields.Length; i++)
        {
            tuples[i] = (fields[i].FieldId, fields[i].Name, fields[i].Type, fields[i].Offset, fields[i].Size, false, false, 0);
        }

        return MakeDefinition(tuples);
    }

    [Test]
    public void IdenticalSchema_ReturnsIdentical()
    {
        var persisted = new[]
        {
            MakeField("Health", 0, FieldType.Int, 0, 4),
            MakeField("Speed", 1, FieldType.Float, 4, 4),
        };
        var comp = MakeComponent(0, 2);
        var def = MakeSimpleDefinition(
            (0, "Health", FieldType.Int, 0, 4),
            (1, "Speed", FieldType.Float, 4, 4));

        var diff = SchemaValidator.ComputeDiff("Test", persisted, comp, def, []);

        Assert.That(diff.Level, Is.EqualTo(CompatibilityLevel.Identical));
        Assert.That(diff.IsIdentical, Is.True);
        Assert.That(diff.FieldChanges, Is.Empty);
        Assert.That(diff.IndexChanges, Is.Empty);
    }

    [Test]
    public void FieldAdded_DetectedAsCompatible()
    {
        var persisted = new[]
        {
            MakeField("Health", 0, FieldType.Int, 0, 4),
        };
        var comp = MakeComponent(0, 1);
        var def = MakeSimpleDefinition(
            (0, "Health", FieldType.Int, 0, 4),
            (1, "Speed", FieldType.Float, 4, 4));

        var diff = SchemaValidator.ComputeDiff("Test", persisted, comp, def, []);

        Assert.That(diff.Level, Is.EqualTo(CompatibilityLevel.Compatible));
        Assert.That(diff.FieldChanges, Has.Count.EqualTo(1));
        Assert.That(diff.FieldChanges[0].Kind, Is.EqualTo(FieldChangeKind.Added));
        Assert.That(diff.FieldChanges[0].FieldName, Is.EqualTo("Speed"));
        Assert.That(diff.FieldChanges[0].FieldId, Is.EqualTo(1));
    }

    [Test]
    public void FieldRemoved_DetectedAsCompatible()
    {
        var persisted = new[]
        {
            MakeField("Health", 0, FieldType.Int, 0, 4),
            MakeField("Speed", 1, FieldType.Float, 4, 4),
        };
        var comp = MakeComponent(0, 2);
        var def = MakeSimpleDefinition(
            (0, "Health", FieldType.Int, 0, 4));

        var diff = SchemaValidator.ComputeDiff("Test", persisted, comp, def, []);

        Assert.That(diff.Level, Is.EqualTo(CompatibilityLevel.Compatible));
        Assert.That(diff.FieldChanges, Has.Count.EqualTo(1));
        Assert.That(diff.FieldChanges[0].Kind, Is.EqualTo(FieldChangeKind.Removed));
        Assert.That(diff.FieldChanges[0].FieldName, Is.EqualTo("Speed"));
    }

    [Test]
    public void TypeWidened_IntToLong()
    {
        var persisted = new[] { MakeField("Score", 0, FieldType.Int, 0, 4) };
        var comp = MakeComponent();
        var def = MakeSimpleDefinition((0, "Score", FieldType.Long, 0, 8));

        var diff = SchemaValidator.ComputeDiff("Test", persisted, comp, def, []);

        Assert.That(diff.Level, Is.EqualTo(CompatibilityLevel.CompatibleWidening));
        var change = diff.FieldChanges.Find(c => c.Kind == FieldChangeKind.TypeWidened);
        Assert.That(change, Is.Not.Null);
        Assert.That(change.OldType, Is.EqualTo(FieldType.Int));
        Assert.That(change.NewType, Is.EqualTo(FieldType.Long));
    }

    [Test]
    public void TypeWidened_FloatToDouble()
    {
        var persisted = new[] { MakeField("Speed", 0, FieldType.Float, 0, 4) };
        var comp = MakeComponent();
        var def = MakeSimpleDefinition((0, "Speed", FieldType.Double, 0, 8));

        var diff = SchemaValidator.ComputeDiff("Test", persisted, comp, def, []);

        Assert.That(diff.Level, Is.EqualTo(CompatibilityLevel.CompatibleWidening));
    }

    [Test]
    public void TypeWidened_String64ToString1024()
    {
        var persisted = new[] { MakeField("Name", 0, FieldType.String64, 0, 64) };
        var comp = MakeComponent();
        var def = MakeSimpleDefinition((0, "Name", FieldType.String1024, 0, 1024));

        var diff = SchemaValidator.ComputeDiff("Test", persisted, comp, def, []);

        Assert.That(diff.Level, Is.EqualTo(CompatibilityLevel.CompatibleWidening));
    }

    [Test]
    public void TypeWidened_Point2FToPoint2D()
    {
        var persisted = new[] { MakeField("Pos", 0, FieldType.Point2F, 0, 8) };
        var comp = MakeComponent();
        var def = MakeSimpleDefinition((0, "Pos", FieldType.Point2D, 0, 16));

        var diff = SchemaValidator.ComputeDiff("Test", persisted, comp, def, []);

        Assert.That(diff.Level, Is.EqualTo(CompatibilityLevel.CompatibleWidening));
    }

    [Test]
    public void TypeWidened_UByteToShort()
    {
        var persisted = new[] { MakeField("Level", 0, FieldType.UByte, 0, 1) };
        var comp = MakeComponent();
        var def = MakeSimpleDefinition((0, "Level", FieldType.Short, 0, 2));

        var diff = SchemaValidator.ComputeDiff("Test", persisted, comp, def, []);

        Assert.That(diff.Level, Is.EqualTo(CompatibilityLevel.CompatibleWidening));
    }

    [Test]
    public void TypeNarrowed_LongToInt_IsBreaking()
    {
        var persisted = new[] { MakeField("Score", 0, FieldType.Long, 0, 8) };
        var comp = MakeComponent();
        var def = MakeSimpleDefinition((0, "Score", FieldType.Int, 0, 4));

        var diff = SchemaValidator.ComputeDiff("Test", persisted, comp, def, []);

        Assert.That(diff.Level, Is.EqualTo(CompatibilityLevel.Breaking));
        Assert.That(diff.HasBreakingChanges, Is.True);
        var change = diff.FieldChanges.Find(c => c.Kind == FieldChangeKind.TypeChanged);
        Assert.That(change, Is.Not.Null);
    }

    [Test]
    public void TypeChanged_IntToFloat_IsBreaking()
    {
        var persisted = new[] { MakeField("Value", 0, FieldType.Int, 0, 4) };
        var comp = MakeComponent();
        var def = MakeSimpleDefinition((0, "Value", FieldType.Float, 0, 4));

        var diff = SchemaValidator.ComputeDiff("Test", persisted, comp, def, []);

        Assert.That(diff.Level, Is.EqualTo(CompatibilityLevel.Breaking));
    }

    [Test]
    public void TypeChanged_SignedToUnsigned_IsBreaking()
    {
        var persisted = new[] { MakeField("Value", 0, FieldType.Int, 0, 4) };
        var comp = MakeComponent();
        var def = MakeSimpleDefinition((0, "Value", FieldType.UInt, 0, 4));

        var diff = SchemaValidator.ComputeDiff("Test", persisted, comp, def, []);

        Assert.That(diff.Level, Is.EqualTo(CompatibilityLevel.Breaking));
    }

    [Test]
    public void OffsetChanged_Detected()
    {
        var persisted = new[]
        {
            MakeField("A", 0, FieldType.Int, 0, 4),
            MakeField("B", 1, FieldType.Int, 4, 4),
        };
        var comp = MakeComponent();
        var def = MakeSimpleDefinition(
            (0, "A", FieldType.Int, 4, 4),
            (1, "B", FieldType.Int, 0, 4));

        var diff = SchemaValidator.ComputeDiff("Test", persisted, comp, def, []);

        Assert.That(diff.Level, Is.EqualTo(CompatibilityLevel.Compatible));
        var offsetChanges = diff.FieldChanges.FindAll(c => c.Kind == FieldChangeKind.OffsetChanged);
        Assert.That(offsetChanges, Has.Count.EqualTo(2));
    }

    [Test]
    public void SizeChanged_ArrayLength_IsBreaking()
    {
        var persisted = new[] { MakeField("Values", 0, FieldType.Int, 0, 4, arrayLength: 1) };
        var comp = MakeComponent();
        var def = MakeDefinition((0, "Values", FieldType.Int, 0, 16, false, false, 4));

        var diff = SchemaValidator.ComputeDiff("Test", persisted, comp, def, []);

        Assert.That(diff.HasBreakingChanges, Is.True);
        var sizeChange = diff.FieldChanges.Find(c => c.Kind == FieldChangeKind.SizeChanged);
        Assert.That(sizeChange, Is.Not.Null);
        Assert.That(sizeChange.Level, Is.EqualTo(CompatibilityLevel.Breaking));
    }

    [Test]
    public void IndexAdded_Compatible()
    {
        var persisted = new[] { MakeField("Score", 0, FieldType.Int, 0, 4, hasIndex: false) };
        var comp = MakeComponent();
        var def = MakeDefinition((0, "Score", FieldType.Int, 0, 4, true, false, 0));

        var diff = SchemaValidator.ComputeDiff("Test", persisted, comp, def, []);

        Assert.That(diff.Level, Is.EqualTo(CompatibilityLevel.Compatible));
        Assert.That(diff.IndexChanges, Has.Count.EqualTo(1));
        Assert.That(diff.IndexChanges[0].Kind, Is.EqualTo(FieldChangeKind.IndexAdded));
    }

    [Test]
    public void IndexRemoved_Compatible()
    {
        var persisted = new[] { MakeField("Score", 0, FieldType.Int, 0, 4, hasIndex: true) };
        var comp = MakeComponent();
        var def = MakeDefinition((0, "Score", FieldType.Int, 0, 4, false, false, 0));

        var diff = SchemaValidator.ComputeDiff("Test", persisted, comp, def, []);

        Assert.That(diff.Level, Is.EqualTo(CompatibilityLevel.Compatible));
        Assert.That(diff.IndexChanges, Has.Count.EqualTo(1));
        Assert.That(diff.IndexChanges[0].Kind, Is.EqualTo(FieldChangeKind.IndexRemoved));
    }

    [Test]
    public void MixedChanges_MaxSeverity()
    {
        var persisted = new[]
        {
            MakeField("Health", 0, FieldType.Int, 0, 4),
            MakeField("Score", 1, FieldType.Long, 4, 8),
        };
        var comp = MakeComponent();
        // Health: Int→Long (widening), Score: Long→Int (breaking)
        var def = MakeSimpleDefinition(
            (0, "Health", FieldType.Long, 0, 8),
            (1, "Score", FieldType.Int, 8, 4));

        var diff = SchemaValidator.ComputeDiff("Test", persisted, comp, def, []);

        Assert.That(diff.Level, Is.EqualTo(CompatibilityLevel.Breaking), "Max severity should be Breaking");
        Assert.That(diff.HasBreakingChanges, Is.True);
    }

    [Test]
    public void IsCompatibleWidening_AllValidPairs()
    {
        var validPairs = new (FieldType, FieldType)[]
        {
            // Signed integer chain
            (FieldType.Byte, FieldType.Short),
            (FieldType.Byte, FieldType.Int),
            (FieldType.Byte, FieldType.Long),
            (FieldType.Short, FieldType.Int),
            (FieldType.Short, FieldType.Long),
            (FieldType.Int, FieldType.Long),
            // Unsigned integer chain
            (FieldType.UByte, FieldType.UShort),
            (FieldType.UByte, FieldType.UInt),
            (FieldType.UByte, FieldType.ULong),
            (FieldType.UShort, FieldType.UInt),
            (FieldType.UShort, FieldType.ULong),
            (FieldType.UInt, FieldType.ULong),
            // Cross-sign: unsigned → wider signed
            (FieldType.UByte, FieldType.Short),
            (FieldType.UByte, FieldType.Int),
            (FieldType.UByte, FieldType.Long),
            (FieldType.UShort, FieldType.Int),
            (FieldType.UShort, FieldType.Long),
            (FieldType.UInt, FieldType.Long),
            // Float
            (FieldType.Float, FieldType.Double),
            // Vectors
            (FieldType.Point2F, FieldType.Point2D),
            (FieldType.Point3F, FieldType.Point3D),
            (FieldType.Point4F, FieldType.Point4D),
            (FieldType.QuaternionF, FieldType.QuaternionD),
            // String
            (FieldType.String64, FieldType.String1024),
        };

        foreach (var (from, to) in validPairs)
        {
            Assert.That(SchemaValidator.IsCompatibleWidening(from, to), Is.True,
                $"Expected {from} → {to} to be a valid widening");
        }
    }

    [Test]
    public void IsCompatibleWidening_InvalidPairs()
    {
        var invalidPairs = new (FieldType, FieldType)[]
        {
            // Reverse/narrowing
            (FieldType.Long, FieldType.Int),
            (FieldType.Double, FieldType.Float),
            (FieldType.Short, FieldType.Byte),
            (FieldType.ULong, FieldType.UInt),
            // Cross-domain
            (FieldType.Int, FieldType.Float),
            (FieldType.Float, FieldType.Int),
            (FieldType.String64, FieldType.Int),
            (FieldType.Int, FieldType.String64),
            // Signed → unsigned (same size — not safe)
            (FieldType.Int, FieldType.UInt),
            (FieldType.Long, FieldType.ULong),
            // Same type
            (FieldType.Int, FieldType.Int),
            // Boolean to numeric
            (FieldType.Boolean, FieldType.Int),
            // String narrowing
            (FieldType.String1024, FieldType.String64),
        };

        foreach (var (from, to) in invalidPairs)
        {
            Assert.That(SchemaValidator.IsCompatibleWidening(from, to), Is.False,
                $"Expected {from} → {to} to NOT be a valid widening");
        }
    }

    [Test]
    public void Rename_IsInformational()
    {
        var persisted = new[] { MakeField("Health", 0, FieldType.Int, 0, 4) };
        var comp = MakeComponent();
        var def = MakeSimpleDefinition((0, "HP", FieldType.Int, 0, 4));
        var renames = new[] { ("Health", "HP", 0) };

        var diff = SchemaValidator.ComputeDiff("Test", persisted, comp, def, renames);

        Assert.That(diff.Level, Is.EqualTo(CompatibilityLevel.InformationOnly));
        var renameChange = diff.FieldChanges.Find(c => c.Kind == FieldChangeKind.Renamed);
        Assert.That(renameChange, Is.Not.Null);
        Assert.That(renameChange.FieldName, Is.EqualTo("HP"));
    }

    [Test]
    public void Summary_FormatsCorrectly()
    {
        var persisted = new[]
        {
            MakeField("Health", 0, FieldType.Int, 0, 4),
            MakeField("Legacy", 1, FieldType.Byte, 4, 1),
        };
        var comp = MakeComponent();
        var def = MakeSimpleDefinition(
            (0, "Health", FieldType.Long, 0, 8),
            (2, "Shield", FieldType.Int, 8, 4));

        var diff = SchemaValidator.ComputeDiff("Test", persisted, comp, def, []);

        Assert.That(diff.Summary, Does.Contain("added"));
        Assert.That(diff.Summary, Does.Contain("removed"));
        Assert.That(diff.Summary, Does.Contain("widened"));
    }

    [Test]
    public void DetailedMessage_ContainsBreakingInfo()
    {
        var persisted = new[] { MakeField("Score", 0, FieldType.Int, 0, 4) };
        var comp = MakeComponent();
        var def = MakeSimpleDefinition((0, "Score", FieldType.String64, 0, 64));

        var diff = SchemaValidator.ComputeDiff("Test", persisted, comp, def, []);

        var message = diff.FormatDetailedMessage();
        Assert.That(message, Does.Contain("Breaking changes"));
        Assert.That(message, Does.Contain("Score"));
        Assert.That(message, Does.Contain("SchemaValidationMode.Skip"));
    }
}
