using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using Typhon.Client;
using Typhon.Protocol;

namespace SwgTatooine.Bots;

/// <summary>A move of credits or items told to this bot's session (<c>PlayerNotice</c>): what the browser's chat says, as the bot counts it.</summary>
public readonly record struct BotNotice(bool Items, bool Incoming, int Mk, ushort Template, int Count, long Amount, long Ref, string Kind, string Other);

/// <summary>
/// The events a bot's session hears: the <c>RealmNews</c> announcements, counted, and the <c>PlayerNotice</c>s, decoded and queued for the bot's own
/// tick. On the session's receive loop, so what it hands over is a concurrent queue.
/// </summary>
/// <remarks>
/// A notice is complete when every field of its plan has arrived: the decoder has no end-of-event call, and it hands the fields over in the CATALOG's
/// order, not the declaration's — closing on the last declared field once dropped the two that sort after it.
/// </remarks>
public sealed class BotEvents : IEventHandler
{
    private long _news;
    private bool _inNotice;
    private int _fields;
    private int _seen;
    private string _other;
    private bool _items;
    private bool _incoming;
    private int _mk;
    private ushort _template;
    private int _count;
    private long _amount;
    private long _ref;
    private string _kind;

    /// <summary><c>RealmNews</c> heard.</summary>
    public long News => Volatile.Read(ref _news);

    /// <summary>The notices heard and not yet taken by the bot.</summary>
    public ConcurrentQueue<BotNotice> Notices { get; } = new();

    public void Event(MessagePlan type)
    {
        _inNotice = type.Name == "PlayerNotice";
        _fields = type.Body.Fields.Length;
        _seen = 0;
        if (type.Name == "RealmNews")
        {
            Interlocked.Increment(ref _news);
        }
    }

    public void Number(FieldPlan field, scoped ReadOnlySpan<double> components)
    {
        if (!_inNotice || components.Length == 0)
        {
            return;
        }

        var v = components[0];
        switch (field.Name)
        {
            case "ledger":
                _items = v == 2;
                break;
            case "incoming":
                _incoming = v != 0;
                break;
            case "mk":
                _mk = (int)v;
                break;
            case "template":
                _template = (ushort)v;
                break;
            case "count":
                _count = (int)v;
                break;
        }

        Seen();
    }

    public void Integer64(FieldPlan field, scoped ReadOnlySpan<ulong> components)
    {
        if (!_inNotice || components.Length == 0)
        {
            return;
        }

        if (field.Name == "amount")
        {
            _amount = (long)components[0];
        }
        else if (field.Name == "ref")
        {
            _ref = (long)components[0];
        }

        Seen();
    }

    public void Text(FieldPlan field, scoped ReadOnlySpan<byte> utf8)
    {
        if (!_inNotice)
        {
            return;
        }

        if (field.Name == "kind")
        {
            _kind = Encoding.UTF8.GetString(utf8);
        }
        else if (field.Name == "other")
        {
            _other = Encoding.UTF8.GetString(utf8);
        }

        Seen();
    }

    // One more field of the notice; the last one closes it.
    private void Seen()
    {
        if (++_seen < _fields)
        {
            return;
        }

        Notices.Enqueue(new BotNotice(_items, _incoming, _mk, _template, _count, _amount, _ref, _kind, _other));
        _inNotice = false;
    }

    public void Bytes(FieldPlan field, scoped ReadOnlySpan<byte> bytes)
    {
    }

    public void List(FieldPlan field, int count, scoped ReadOnlySpan<double> components)
    {
    }
}
