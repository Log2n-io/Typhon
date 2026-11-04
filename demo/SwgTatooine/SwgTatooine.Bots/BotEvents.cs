using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using Typhon.Client;
using Typhon.Protocol;

namespace SwgTatooine.Bots;

/// <summary>A move of credits or items told to this bot's session (<c>PlayerNotice</c>): what the browser's chat says, as the bot counts it.</summary>
public readonly record struct BotNotice(bool Items, bool Incoming, int Mk, ushort Template, int Count, long Amount, long Ref, string Kind, string Other);

/// <summary>A mail arrived for this bot's character (<c>MailNotice</c>, mail.md § 3): its id, its kind, the unread count, who from, the subject.</summary>
public readonly record struct BotMailNotice(ulong Mail, int Kind, int Unread, string From, string Subject);

/// <summary>A /tell this bot's session heard (<c>Whisper</c>): from someone, or its own echo (<see cref="Outgoing"/>).</summary>
public readonly record struct BotWhisper(string Other, string Text, bool Outgoing);

/// <summary>
/// The events a bot's session hears: the <c>RealmNews</c> announcements, counted; the <c>PlayerNotice</c>s, <c>MailNotice</c>s and <c>Whisper</c>s, decoded
/// and queued for the bot's own tick. On the session's receive loop, so what it hands over is a concurrent queue.
/// </summary>
/// <remarks>
/// An event is complete when every field of its plan has arrived: the decoder has no end-of-event call, and it hands the fields over in the CATALOG's
/// order, not the declaration's — closing on the last declared field once dropped the two that sort after it.
/// </remarks>
public sealed class BotEvents : IEventHandler
{
    private const int None = 0;
    private const int Notice = 1;
    private const int MailArrived = 2;
    private const int Whispered = 3;

    private long _news;
    private int _event;
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
    private string _subject;
    private string _text;

    /// <summary><c>RealmNews</c> heard.</summary>
    public long News => Volatile.Read(ref _news);

    /// <summary>The notices heard and not yet taken by the bot.</summary>
    public ConcurrentQueue<BotNotice> Notices { get; } = new();

    /// <summary>The mails told and not yet taken by the bot.</summary>
    public ConcurrentQueue<BotMailNotice> Mail { get; } = new();

    /// <summary>The /tells heard and not yet taken by the bot.</summary>
    public ConcurrentQueue<BotWhisper> Whispers { get; } = new();

    public void Event(MessagePlan type)
    {
        _event = type.Name switch
        {
            "PlayerNotice" => Notice,
            "MailNotice" => MailArrived,
            "Whisper" => Whispered,
            _ => None,
        };
        _fields = type.Body.Fields.Length;
        _seen = 0;
        _other = _kind = _subject = _text = string.Empty;
        _items = _incoming = false;
        _mk = _count = 0;
        _template = 0;
        _amount = _ref = 0;
        if (type.Name == "RealmNews")
        {
            Interlocked.Increment(ref _news);
        }
    }

    public void Number(FieldPlan field, scoped ReadOnlySpan<double> components)
    {
        if (_event == None || components.Length == 0)
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
            case "outgoing":
                _incoming = v != 0;
                break;
            case "mk":
                _mk = (int)v;
                break;
            case "kind" when _event == MailArrived:
                _mk = (int)v;
                break;
            case "template":
                _template = (ushort)v;
                break;
            case "count":
            case "unread":
                _count = (int)v;
                break;
        }

        Seen();
    }

    public void Integer64(FieldPlan field, scoped ReadOnlySpan<ulong> components)
    {
        if (_event == None || components.Length == 0)
        {
            return;
        }

        if (field.Name == "amount")
        {
            _amount = (long)components[0];
        }
        else if (field.Name is "ref" or "mail")
        {
            _ref = (long)components[0];
        }

        Seen();
    }

    public void Text(FieldPlan field, scoped ReadOnlySpan<byte> utf8)
    {
        if (_event == None)
        {
            return;
        }

        // Decoded only where it is kept: a notice's item name, which the bot does not read, is never turned into a string.
        switch (field.Name)
        {
            case "kind":
                _kind = Encoding.UTF8.GetString(utf8);
                break;
            case "other":
            case "from":
                _other = Encoding.UTF8.GetString(utf8);
                break;
            case "subject":
                _subject = Encoding.UTF8.GetString(utf8);
                break;
            case "text":
                _text = Encoding.UTF8.GetString(utf8);
                break;
        }

        Seen();
    }

    // One more field of the event; the last one closes it.
    private void Seen()
    {
        if (++_seen < _fields)
        {
            return;
        }

        switch (_event)
        {
            case Notice:
                Notices.Enqueue(new BotNotice(_items, _incoming, _mk, _template, _count, _amount, _ref, _kind, _other));
                break;
            case MailArrived:
                Mail.Enqueue(new BotMailNotice((ulong)_ref, _mk, _count, _other, _subject));
                break;
            case Whispered:
                Whispers.Enqueue(new BotWhisper(_other, _text, _incoming));
                break;
        }

        _event = None;
    }

    // Counted though never read: an event closes when every field of its plan has arrived, whatever its codec.
    public void Bytes(FieldPlan field, scoped ReadOnlySpan<byte> bytes)
    {
        if (_event != None)
        {
            Seen();
        }
    }

    public void List(FieldPlan field, int count, scoped ReadOnlySpan<double> components)
    {
        if (_event != None)
        {
            Seen();
        }
    }
}
