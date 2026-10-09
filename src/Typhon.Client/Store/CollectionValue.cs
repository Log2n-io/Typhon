using System;
using Typhon.Protocol;

namespace Typhon.Client;

/// <summary>
/// One entity's collection (W34): how many elements it holds, how many the server sent, and the elements as structure-of-arrays over the element's fields.
/// </summary>
/// <remarks>
/// <para>
/// <b>A whole list per change.</b> The server re-sends a collection whole when it changes, so a decode overwrites it: <see cref="Count"/> elements are the
/// list, and nothing past them means anything. <see cref="Total"/> above <see cref="Count"/> says the server truncated it at its <c>maxCount</c>.
/// </para>
/// <para>
/// <b>Columns, as an archetype's.</b> An element field's numbers are one array of <c>capacity × components</c>, its 64-bit integers likewise (bit patterns),
/// its text one string per element. The capacity grows to the largest list received and is kept, so a steady stream of lists of one size allocates nothing
/// but the strings of the text that changed.
/// </para>
/// </remarks>
public sealed class CollectionValue
{
    internal CollectionValue(FieldPlan field)
    {
        Field = field;
        var fields = field.ElementSection.Fields;
        Numbers = new double[fields.Length][];
        Integers = new ulong[fields.Length][];
        Texts = new string[fields.Length][];
        Grow(1);
    }

    /// <summary>The collection field; its <see cref="FieldPlan.ElementSection"/> names the element's fields by ordinal.</summary>
    public FieldPlan Field { get; }

    /// <summary>How many elements the entity holds — above <see cref="Count"/> when the server truncated the list.</summary>
    public int Total { get; private set; }

    /// <summary>How many elements were sent and are held here, in <c>[0, Count)</c>.</summary>
    public int Count { get; private set; }

    /// <summary>Whether the list arrived cut at the codec's <c>maxCount</c>.</summary>
    public bool Truncated => Count < Total;

    /// <summary>Per element field ordinal: <c>capacity × components</c> numbers, or <see langword="null"/> for a field that is not numeric.</summary>
    public double[][] Numbers { get; }

    /// <summary>Per element field ordinal: <c>capacity × components</c> 64-bit integers as bit patterns, or <see langword="null"/>.</summary>
    public ulong[][] Integers { get; }

    /// <summary>Per element field ordinal: the text per element, or <see langword="null"/> for a field that is not text.</summary>
    public string[][] Texts { get; }

    /// <summary>Elements allocated.</summary>
    public int Capacity { get; private set; }

    /// <summary>An element field's first number, by name; for a test or a tool, not a loop.</summary>
    /// <param name="index">The element.</param>
    /// <param name="field">The element field's wire name.</param>
    /// <returns>The value.</returns>
    public double Number(int index, string field)
    {
        var plan = Find(index, field);
        var column = Numbers[plan.Ordinal] ?? throw new ArgumentException($"element field '{field}' is not numeric", nameof(field));
        return column[index * plan.Components];
    }

    /// <summary>An element field's text, by name; for a test or a tool, not a loop.</summary>
    /// <param name="index">The element.</param>
    /// <param name="field">The element field's wire name.</param>
    /// <returns>The text.</returns>
    public string Text(int index, string field) =>
        (Texts[Find(index, field).Ordinal] ?? throw new ArgumentException($"element field '{field}' is not text", nameof(field)))[index];

    /// <summary>An element field's first 64-bit integer, by name, as its bit pattern.</summary>
    /// <param name="index">The element.</param>
    /// <param name="field">The element field's wire name.</param>
    /// <returns>The value.</returns>
    public ulong Integer64(int index, string field)
    {
        var plan = Find(index, field);
        var column = Integers[plan.Ordinal] ?? throw new ArgumentException($"element field '{field}' is not a 64-bit integer", nameof(field));
        return column[index * plan.Components];
    }

    internal void Begin(int total, int sent)
    {
        if (sent > Capacity)
        {
            var capacity = Math.Max(Capacity, 1);
            while (capacity < sent)
            {
                capacity *= 2;
            }

            Grow(capacity);
        }

        Total = total;
        Count = sent;
    }

    internal void Reset()
    {
        Total = 0;
        Count = 0;
    }

    private FieldPlan Find(int index, string field)
    {
        if ((uint)index >= (uint)Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"the collection holds {Count} element(s)");
        }

        return Array.Find(Field.ElementSection.Fields, f => f.Name == field) ?? throw new ArgumentException($"no element field '{field}'", nameof(field));
    }

    private void Grow(int capacity)
    {
        var fields = Field.ElementSection.Fields;
        for (var f = 0; f < fields.Length; f++)
        {
            var field = fields[f];
            switch (field.ValueKind)
            {
                case FieldValueKind.Number:
                    Numbers[f] = Resize(Numbers[f] ?? [], capacity * field.Components);
                    break;
                case FieldValueKind.Integer64:
                    Integers[f] = Resize(Integers[f] ?? [], capacity * field.Components);
                    break;
                case FieldValueKind.Text:
                    Texts[f] = Resize(Texts[f] ?? [], capacity);
                    break;
            }
        }

        Capacity = capacity;
    }

    private static T[] Resize<T>(T[] array, int length)
    {
        var next = new T[length];
        array.AsSpan().CopyTo(next);
        return next;
    }
}
