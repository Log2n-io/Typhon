using System;
using System.Collections.Generic;
using System.Text;
using Typhon.Protocol;

namespace Typhon.Client;

/// <summary>One event as received: its type and its fields by wire name. Entity fields hold the netId the session knows (0 when it knows none).</summary>
public sealed class ReceivedEvent
{
    internal ReceivedEvent(MessagePlan type) => Type = type;

    /// <summary>The event type, from the catalog.</summary>
    public MessagePlan Type { get; }

    /// <summary>The type's catalog name.</summary>
    public string Name => Type.Name;

    /// <summary>Numeric fields (every codec that decodes to doubles, entity references included), by wire name, each its components.</summary>
    public Dictionary<string, double[]> Numbers { get; } = new();

    /// <summary>64-bit integer fields (<c>u64 i64 varu64 vari64</c>), by wire name, as raw bits.</summary>
    public Dictionary<string, ulong[]> Integers { get; } = new();

    /// <summary>Text fields, by wire name.</summary>
    public Dictionary<string, string> Texts { get; } = new();

    /// <summary>Byte fields, by wire name.</summary>
    public Dictionary<string, byte[]> Bytes { get; } = new();

    /// <summary>The first component of a numeric or integer field, or <paramref name="fallback"/> when the event has no such field.</summary>
    /// <param name="field">The wire name.</param>
    /// <param name="fallback">The value when absent.</param>
    /// <returns>The value; a 64-bit integer is read as signed.</returns>
    public double Number(string field, double fallback = 0) =>
        Numbers.TryGetValue(field, out var n) && n.Length > 0 ? n[0]
        : Integers.TryGetValue(field, out var i) && i.Length > 0 ? unchecked((long)i[0])
        : fallback;
}

/// <summary>
/// An <see cref="IEventHandler"/> that keeps each frame's events as <see cref="ReceivedEvent"/> values: what an application without a decoder of its own
/// passes as <see cref="ClientOptions.Events"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Threading.</b> Events are decoded on the thread that applies frames, during the apply. Take a frame's events in
/// <see cref="TyphonClient.FrameApplied"/> — the one point the frame is whole — with <see cref="TakeFrame"/>, and hand them to whatever thread acts on
/// them; a frame's events not taken there join the next frame's. Not thread-safe otherwise: only the applying thread touches it.
/// </para>
/// <para>
/// <b>Allocates</b> per event (the value and its arrays). For a game client receiving a few events a frame that is nothing; a decoder on a hot path
/// implements <see cref="IEventHandler"/> itself. It is also the way to receive events from code that cannot implement <see cref="IFieldSink"/>, whose
/// parameters are <c>scoped</c>: a Unity script (C# 9) is one.
/// </para>
/// </remarks>
public sealed class EventCollector : IEventHandler
{
    private readonly List<ReceivedEvent> _frame = [];
    private ReceivedEvent _current;

    /// <summary>The events decoded since the last <see cref="TakeFrame"/>, in emission order.</summary>
    public IReadOnlyList<ReceivedEvent> Pending => _frame;

    /// <summary>Moves the events decoded since the last call into <paramref name="into"/>, in emission order.</summary>
    /// <param name="into">Where they go.</param>
    public void TakeFrame(List<ReceivedEvent> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        into.AddRange(_frame);
        _frame.Clear();
        _current = null;
    }

    void IEventHandler.Event(MessagePlan type)
    {
        _current = new ReceivedEvent(type);
        _frame.Add(_current);
    }

    void IFieldSink.Number(FieldPlan field, scoped ReadOnlySpan<double> components)
    {
        if (_current != null)
        {
            _current.Numbers[field.Name] = components.ToArray();
        }
    }

    void IFieldSink.Integer64(FieldPlan field, scoped ReadOnlySpan<ulong> components)
    {
        if (_current != null)
        {
            _current.Integers[field.Name] = components.ToArray();
        }
    }

    void IFieldSink.Text(FieldPlan field, scoped ReadOnlySpan<byte> utf8)
    {
        if (_current != null)
        {
            _current.Texts[field.Name] = Encoding.UTF8.GetString(utf8);
        }
    }

    void IFieldSink.Bytes(FieldPlan field, scoped ReadOnlySpan<byte> bytes)
    {
        if (_current != null)
        {
            _current.Bytes[field.Name] = bytes.ToArray();
        }
    }

    void IFieldSink.List(FieldPlan field, int count, scoped ReadOnlySpan<double> components)
    {
        if (_current != null)
        {
            _current.Numbers[field.Name] = components.ToArray();
        }
    }
}
