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
/// A horde's status (<c>HordeStatus</c>, horde.md § 4): the event, its phase, the help still needed in 255ths (255 is enough), the seconds to its start, the
/// raiders alive, the defenders committed and present, and the target the raiders go to and the defenders rally at.
/// </summary>
public readonly record struct BotHordeStatus(ulong Event, int Phase, int City, int Help, int StartsInS, int Raiders, int Committed, int Present, float X, float Z)
{
    /// <summary>The fortress, and the town hall the raiders break (horde.md § 10).</summary>
    public float FortX { get; init; }

    public float FortZ { get; init; }

    public float HallX { get; init; }

    public float HallZ { get; init; }

    /// <summary>The town hall's and the fortress's hit points, 1 + 254ths (1 broken), 0 none.</summary>
    public int HallHp { get; init; }

    public int FortHp { get; init; }
}

/// <summary>Where the raiders are (<c>HordeRadar</c>, horde.md § 10): up to eight clusters, the most crowded first.</summary>
public sealed record BotHordeRadar(ulong Event, float[] X, float[] Z, int[] N);

/// <summary>To a character that answered a horde (<c>HordeRally</c>): where to go, and its commitment's state (0 when the event is over).</summary>
public readonly record struct BotHordeRally(ulong Event, float X, float Z, int State);

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
    private const int HordeHeard = 4;
    private const int RallyHeard = 5;
    private const int RadarHeard = 6;
    private readonly float[] _rx = new float[8];
    private readonly float[] _rz = new float[8];
    private readonly int[] _rn = new int[8];
    private int _radarCount;
    private float _fx;
    private float _fz;
    private float _hx;
    private float _hz;
    private int _hallHp;
    private int _fortHp;

    // A horde status's or a rally's fields, by name (horde.md § 4-5).
    private ulong _hordeEvent;
    private int _phase;
    private int _city;
    private int _help;
    private int _startsIn;
    private int _raiders;
    private int _committed;
    private int _present;
    private int _state;
    private float _x;
    private float _z;

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

    /// <summary>The horde statuses heard and not yet taken by the bot.</summary>
    public ConcurrentQueue<BotHordeStatus> Hordes { get; } = new();

    /// <summary>The rally points told and not yet taken by the bot.</summary>
    public ConcurrentQueue<BotHordeRally> Rallies { get; } = new();

    /// <summary>Radars heard (<c>HordeRadar</c>), for the bot's tick to take.</summary>
    public ConcurrentQueue<BotHordeRadar> Radars { get; } = new();

    public void Event(MessagePlan type)
    {
        _event = type.Name switch
        {
            "PlayerNotice" => Notice,
            "MailNotice" => MailArrived,
            "Whisper" => Whispered,
            "HordeStatus" => HordeHeard,
            "HordeRally" => RallyHeard,
            "HordeRadar" => RadarHeard,
            _ => None,
        };
        _fx = _fz = _hx = _hz = 0f;
        _hallHp = _fortHp = _radarCount = 0;
        _hordeEvent = 0;
        _phase = _city = _help = _startsIn = _raiders = _committed = _present = _state = 0;
        _x = _z = 0f;
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
        if (_event == RadarHeard)
        {
            var name = field.Name;
            if (name == "count")
            {
                _radarCount = Math.Clamp((int)v, 0, 8);
            }
            else if (name.Length == 2 && name[1] is >= '0' and <= '7')
            {
                var i = name[1] - '0';
                switch (name[0])
                {
                    case 'x':
                        _rx[i] = (float)v;
                        break;
                    case 'z':
                        _rz[i] = (float)v;
                        break;
                    case 'n':
                        _rn[i] = (int)v;
                        break;
                }
            }

            Seen();
            return;
        }

        if (_event is HordeHeard or RallyHeard)
        {
            switch (field.Name)
            {
                case "phase":
                    _phase = (int)v;
                    break;
                case "city":
                    _city = (int)v;
                    break;
                case "help":
                    _help = (int)v;
                    break;
                case "startsIn":
                    _startsIn = (int)v;
                    break;
                case "raiders":
                    _raiders = (int)v;
                    break;
                case "committed":
                    _committed = (int)v;
                    break;
                case "present":
                    _present = (int)v;
                    break;
                case "state":
                    _state = (int)v;
                    break;
                case "x":
                    _x = (float)v;
                    break;
                case "z":
                    _z = (float)v;
                    break;
                case "fx":
                    _fx = (float)v;
                    break;
                case "fz":
                    _fz = (float)v;
                    break;
                case "hx":
                    _hx = (float)v;
                    break;
                case "hz":
                    _hz = (float)v;
                    break;
                case "hall":
                    _hallHp = (int)v;
                    break;
                case "fort":
                    _fortHp = (int)v;
                    break;
            }

            Seen();
            return;
        }

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
        else if (field.Name == "event")
        {
            _hordeEvent = components[0];
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
            case HordeHeard:
                Hordes.Enqueue(new BotHordeStatus(_hordeEvent, _phase, _city, _help, _startsIn, _raiders, _committed, _present, _x, _z)
                {
                    FortX = _fx,
                    FortZ = _fz,
                    HallX = _hx,
                    HallZ = _hz,
                    HallHp = _hallHp,
                    FortHp = _fortHp,
                });
                break;
            case RadarHeard:
                Radars.Enqueue(new BotHordeRadar(_hordeEvent, _rx[.._radarCount], _rz[.._radarCount], _rn[.._radarCount]));
                break;
            case RallyHeard:
                Rallies.Enqueue(new BotHordeRally(_hordeEvent, _x, _z, _state));
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
