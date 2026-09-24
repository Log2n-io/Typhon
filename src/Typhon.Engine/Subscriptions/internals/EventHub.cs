using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Typhon.Protocol;

namespace Typhon.Engine.Internals;

/// <summary>Where one wire field of an event is read from inside the event struct, resolved once at <c>Start</c> (09 § 11).</summary>
internal readonly struct EventFieldBinding
{
    public EventFieldBinding(FieldPlan field, int offset, int components, CommandFieldElement element, int elementSize, bool entity)
    {
        Field = field;
        Offset = offset;
        Components = components;
        Element = element;
        ElementSize = elementSize;
        Entity = entity;
    }

    /// <summary>The wire field, in the catalog's wire order.</summary>
    public FieldPlan Field { get; }

    /// <summary>Byte offset of the struct field.</summary>
    public int Offset { get; }

    /// <summary>Numbers the codec takes: 1 for a scalar, 2 or 3 for a vector.</summary>
    public int Components { get; }

    /// <summary>How each number is stored.</summary>
    public CommandFieldElement Element { get; }

    /// <summary>Bytes each stored number occupies.</summary>
    public int ElementSize { get; }

    /// <summary>An <see cref="EntityId"/>: it travels as the entity's netId, 0 when it has none.</summary>
    public bool Entity { get; }

    /// <summary>Reads the stored numbers.</summary>
    public void Load(ReadOnlySpan<byte> payload, Span<double> into)
    {
        var source = payload.Slice(Offset, Components * ElementSize);
        for (var i = 0; i < Components; i++)
        {
            var slot = source.Slice(i * ElementSize, ElementSize);
            into[i] = Element switch
            {
                CommandFieldElement.I8 => (sbyte)slot[0],
                CommandFieldElement.U8 => slot[0],
                CommandFieldElement.I16 => MemoryMarshal.Read<short>(slot),
                CommandFieldElement.U16 => MemoryMarshal.Read<ushort>(slot),
                CommandFieldElement.I32 => MemoryMarshal.Read<int>(slot),
                CommandFieldElement.U32 => MemoryMarshal.Read<uint>(slot),
                CommandFieldElement.I64 => MemoryMarshal.Read<long>(slot),
                CommandFieldElement.U64 => MemoryMarshal.Read<ulong>(slot),
                CommandFieldElement.F32 => MemoryMarshal.Read<float>(slot),
                _ => MemoryMarshal.Read<double>(slot),
            };
        }
    }
}

/// <summary>Reads a <see cref="EventRouting.Near"/> event's point from its payload; built once, in the declaring generic context.</summary>
internal delegate Vector3D EventPointReader(ReadOnlySpan<byte> payload);

/// <summary>Resolves a live entity an event names: its netId and v̂ (09 § 11).</summary>
internal interface IEventEntities
{
    bool TryResolve(EntityId entity, out uint netId, out float x, out float y, out float z);
}

/// <summary>A session's geometry for the geometric routes: the cells it spans, and whether it sees a point (09 § 11).</summary>
internal interface IEventGeometry
{
    /// <summary>A World session: it sees a point whose cell it has delivered, and no cell box bounds that.</summary>
    bool World { get; }

    void CellBox(out int minCx, out int maxCx, out int minCy, out int maxCy, out int minCz, out int maxCz);

    bool Sees(float x, float y, float z, float viewRadius);
}

/// <summary>A worker's scratch for one session's events: the (tick slot, event) pairs it will write.</summary>
internal sealed class EventPicks
{
    public long[] Items = new long[64];
    public int Count;
    public int[] Temp = new int[64];
    public int TempCount;

    public void Clear()
    {
        Count = 0;
        TempCount = 0;
    }

    public void AddTemp(int e)
    {
        if (TempCount == Temp.Length)
        {
            Array.Resize(ref Temp, TempCount * 2);
        }

        Temp[TempCount++] = e;
    }

    public void Add(long pick)
    {
        if (Count == Items.Length)
        {
            Array.Resize(ref Items, Count * 2);
        }

        Items[Count++] = pick;
    }
}

/// <summary>A declared event, compiled against the catalog: its wire index, routing, and field bindings in wire order.</summary>
internal sealed class EventTypeInfo
{
    public EventTypeInfo(string name, int index, int wireIdx, EventRouting routing, int[] entityOffsets, EventPointReader point, float nearRadius,
        int payloadSize, SectionPlan body, EventFieldBinding[] bindings)
    {
        Name = name;
        Index = index;
        WireIdx = wireIdx;
        Routing = routing;
        EntityOffsets = entityOffsets;
        Point = point;
        NearRadius = nearRadius;
        PayloadSize = payloadSize;
        Body = body;
        Bindings = bindings;
        var max = 5 + body.PackBytes + 8;
        foreach (var b in bindings)
        {
            max += b.Field.Packed ? 0 : 5 * Math.Max(1, b.Components);
        }

        MaxBytes = max;
    }

    public string Name { get; }

    /// <summary>The declaration's position in the registry: the index an emission record carries.</summary>
    public int Index { get; }

    public int WireIdx { get; }

    public EventRouting Routing { get; }

    /// <summary>The routing entities' offsets: the owner for <see cref="EventRouting.ToOwner"/>, each named entity for <see cref="EventRouting.ToKnown"/>.</summary>
    public int[] EntityOffsets { get; }

    /// <summary>The point of a <see cref="EventRouting.Near"/> event.</summary>
    public EventPointReader Point { get; }

    /// <summary>A <see cref="EventRouting.Near"/> event's viewpoint radius; 0 for none.</summary>
    public float NearRadius { get; }

    public int PayloadSize { get; }

    public SectionPlan Body { get; }

    public EventFieldBinding[] Bindings { get; }

    /// <summary>The most bytes one encoded event of this type takes: its index, its pack, and five bytes per number.</summary>
    public int MaxBytes { get; }
}

/// <summary>
/// Server events to clients (09 § 11, SUB-21): what systems <c>Emit</c> this tick, encoded once, routed per session, and kept for the sessions that miss
/// frames.
/// </summary>
/// <remarks>
/// <para>
/// <b>Emission.</b> Each worker slot appends to its own segment — <c>[int type][u64 target][payload]</c> records in a managed buffer that only grows past its
/// high-water mark — so <c>Emit</c> takes no lock, and a tick's order is the slots' order, then each slot's call order.
/// </para>
/// <para>
/// <b>Encode once.</b> The frame prologue encodes every record into the tick's arena, through the catalog's own field plans and
/// <see cref="FieldCodec.WriteNumber"/>: the bytes are, by construction, what a client decodes, and the same for every session. An <see cref="EntityId"/>
/// travels as the entity's netId — resolved live, or from this tick's departed entities (a destroyed one still names its netId, Q7) — 0 when it has none.
/// A value its codec cannot carry drops that event, counted in <see cref="Rejected"/>: the tick path does not throw.
/// </para>
/// <para>
/// <b>Routing.</b> <c>Broadcast</c> to every session; <c>ToOwner</c> and <c>ToSession</c> through two lists sorted once per tick, each session
/// binary-searching its own key — its controlled entity, its identity. <c>Near</c> and <c>ToKnown</c> are geometric: one entry per point (the event's, or
/// each named entity's v̂) filed by cell and sorted once; a session searches only its cell box's rows and asks its own geometry whether it sees the point —
/// the push step's known-set test, against its committed or pending geometry. A session's matches are deduplicated and written in emission order.
/// </para>
/// <para>
/// <b>Skips: a log, not buffers.</b> The last <see cref="PushReplication.LogDepth"/> ticks' arenas and routing stay, and a session's frame carries every tick
/// since its last one. Past the log, a <see cref="SummaryDepth"/>-tick summary of the routing (no bytes) counts what the session missed — the geometric
/// routes against its geometry now — sent first as the built-in <c>EventsLost</c>; beyond the summary the count is a lower bound.
/// </para>
/// </remarks>
internal sealed class EventHub
{
    /// <summary>How many ticks the loss count reaches back.</summary>
    public const int SummaryDepth = 256;

    private const int RecordHeader = sizeof(int) + sizeof(ulong);

    private readonly EventTypeInfo[] _types;
    private readonly Dictionary<Type, EventTypeInfo> _byType = [];
    private readonly Dictionary<long, NetIdLeaseSet.DepartedEntity> _departed = [];
    private Segment[] _segments = [new Segment()];

    private readonly Tick[] _log = new Tick[PushReplication.LogDepth];
    private readonly Tick[] _summaries = new Tick[SummaryDepth];

    // The last tick that encoded any event: a frame whose range starts after it has none to look for.
    private uint _lastNonEmpty;

    /// <summary>Events dropped at encode — a value its codec cannot carry, or a routing point that threw — cumulative.</summary>
    public long Rejected;

    /// <summary>
    /// Emissions of ticks no frame stage encoded — no session was open, no profile observes an archetype, or the tick aborted — discarded at the next tick's
    /// start rather than held: their bytes, cumulative.
    /// </summary>
    public long DiscardedBytes;

    // The last tick EncodeTick ran for: a tick that starts after one that did not encode discards what that one emitted.
    private uint _lastEncoded;

    /// <summary>Events encoded — cumulative.</summary>
    public long Encoded;

    /// <summary>Events written into frames, <c>EventsLost</c> included — cumulative.</summary>
    public long Delivered;

    /// <summary>Events counted into <c>EventsLost</c> — cumulative.</summary>
    public long Lost;

    /// <summary>Time spent encoding, serial, in the frame prologue — cumulative, in Stopwatch ticks.</summary>
    public long EncodeTicks;

    private EventHub(EventTypeInfo[] types)
    {
        _types = types;
        for (var i = 0; i < _log.Length; i++)
        {
            _log[i] = new Tick();
        }

        for (var i = 0; i < _summaries.Length; i++)
        {
            _summaries[i] = new Tick();
        }
    }

    /// <summary>Compiles the declared events against the catalog; <see langword="null"/> when none is declared.</summary>
    /// <exception cref="InvalidOperationException">An event's field cannot be read from its struct.</exception>
    public static EventHub Build(SubscriptionsRegistry registry, CatalogPlan plan)
    {
        if (registry.Events.Count == 0)
        {
            return null;
        }

        var types = new EventTypeInfo[registry.Events.Count];
        for (var i = 0; i < types.Length; i++)
        {
            types[i] = Compile(registry.Events[i], plan);
        }

        var hub = new EventHub(types);
        for (var i = 0; i < types.Length; i++)
        {
            hub._byType[registry.Events[i].EventType] = types[i];
        }

        return hub;
    }

    private static EventTypeInfo Compile(EventDeclaration declaration, CatalogPlan plan)
    {
        var type = declaration.EventType;
        var message = plan.EventByName(declaration.Name)
                      ?? throw new InvalidOperationException($"Event '{declaration.Name}' is not in the catalog it was declared into.");
        int marshalled;
        try
        {
            marshalled = Marshal.SizeOf(type);
        }
        catch (ArgumentException)
        {
            marshalled = -1;
        }

        // Fields are read by their marshalled offsets from the struct's managed bytes: the two layouts must be one, which a bool or a char anywhere breaks.
        if (marshalled != declaration.PayloadSize || !IsBlittable(type))
        {
            throw new InvalidOperationException(
                $"Event '{declaration.Name}': '{type.Name}' is not blittable (a bool, a char, or a type with automatic layout), so its fields cannot be read " +
                "by offset. Carry a flag as a byte.");
        }

        if (message.Body.PackBytes > MaxPackBytes)
        {
            throw new InvalidOperationException(
                $"Event '{declaration.Name}' packs {message.Body.PackBytes} bytes of flags and bit fields; an event packs at most {MaxPackBytes}.");
        }

        var fields = message.Body.Fields;
        var bindings = new EventFieldBinding[fields.Length];
        for (var i = 0; i < fields.Length; i++)
        {
            bindings[i] = Bind(declaration, type, fields[i]);
        }

        var entities = new int[declaration.RoutingEntityFields.Count];
        for (var i = 0; i < entities.Length; i++)
        {
            entities[i] = (int)Marshal.OffsetOf(type, declaration.RoutingEntityFields[i]);
        }

        return new EventTypeInfo(declaration.Name, declaration.Index, message.Idx, declaration.Routing, entities, declaration.RoutingPointReader,
            (float)declaration.RoutingRadiusM, declaration.PayloadSize, message.Body, bindings);
    }

    /// <summary>The most bytes an event's leading bit pack may take.</summary>
    public const int MaxPackBytes = 64;

    private static bool IsBlittable(Type type)
    {
        foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            var t = f.FieldType.IsEnum ? Enum.GetUnderlyingType(f.FieldType) : f.FieldType;
            if (t == typeof(bool) || t == typeof(char) || (t.IsValueType && !t.IsPrimitive && !IsBlittable(t)))
            {
                return false;
            }
        }

        return !type.IsAutoLayout;
    }

    private static EventFieldBinding Bind(EventDeclaration declaration, Type type, FieldPlan field)
    {
        if (field.ValueKind != FieldValueKind.Number)
        {
            throw new InvalidOperationException(
                $"Event '{declaration.Name}' field '{field.Name}' travels as {field.ValueKind}, and an event is an unmanaged struct, which holds no text, no " +
                "blob and no list.");
        }

        var sourceName = field.Name;
        foreach (var declared in declaration.Fields)
        {
            if (string.Equals(declared.Name, field.Name, StringComparison.Ordinal))
            {
                sourceName = declared.SourceFieldName;
                break;
            }
        }

        var member = type.GetField(sourceName, BindingFlags.Public | BindingFlags.Instance)
                     ?? throw new InvalidOperationException($"Event '{declaration.Name}' carries '{field.Name}', and '{type.Name}' has no field '{sourceName}'.");
        var offset = (int)Marshal.OffsetOf(type, sourceName);
        if (member.FieldType == typeof(EntityId))
        {
            return new EventFieldBinding(field, offset, 1, CommandFieldElement.U64, sizeof(ulong), entity: true);
        }

        var (element, elementSize) = CommandRegistry.ElementOf(member.FieldType, declaration.Name, field.Name);
        var fieldSize = Marshal.SizeOf(member.FieldType.IsEnum ? Enum.GetUnderlyingType(member.FieldType) : member.FieldType);
        if (fieldSize != field.Components * elementSize)
        {
            throw new InvalidOperationException(
                $"Event '{declaration.Name}' field '{field.Name}' encodes {field.Components} number(s) of {elementSize} bytes, and '{type.Name}.{sourceName}' " +
                $"is {fieldSize} bytes.");
        }

        return new EventFieldBinding(field, offset, field.Components, element, elementSize, entity: false);
    }

    // ══ Emission (any worker) ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Sizes the segments for the scheduler's worker slots, plus slot 0 for callers outside a worker. Before the first tick.</summary>
    public void BindWorkerSlots(int workerSlots)
    {
        var segments = new Segment[workerSlots + 1];
        for (var i = 0; i < segments.Length; i++)
        {
            segments[i] = i < _segments.Length ? _segments[i] : new Segment();
        }

        _segments = segments;
    }

    /// <summary>Records an event from segment <paramref name="slot"/>'s worker.</summary>
    public void Emit<T>(int slot, in T evt) where T : unmanaged
    {
        var info = InfoOf<T>();
        if (info.Routing == EventRouting.ToSession)
        {
            throw new InvalidOperationException($"'{typeof(T).Name}' routes to a session: emit it with EmitTo(session, …).");
        }

        Record(slot, info, 0UL, in evt);
    }

    /// <summary>Records an event addressed to one session.</summary>
    public void EmitTo<T>(int slot, SessionId session, in T evt) where T : unmanaged
    {
        var info = InfoOf<T>();
        if (info.Routing != EventRouting.ToSession)
        {
            throw new InvalidOperationException($"'{typeof(T).Name}' does not route to a session (RouteToSession); emit it with Emit(…).");
        }

        Record(slot, info, session.Value, in evt);
    }

    private EventTypeInfo InfoOf<T>() where T : unmanaged =>
        _byType.TryGetValue(typeof(T), out var info)
            ? info
            : throw new InvalidOperationException($"'{typeof(T).Name}' is not a declared event: declare it with Subscriptions.Event<{typeof(T).Name}>(…).");

    private void Record<T>(int slot, EventTypeInfo info, ulong target, in T evt) where T : unmanaged
    {
        var segments = _segments;
        System.Diagnostics.Debug.Assert((uint)slot < (uint)segments.Length, "a worker slot past the segments BindWorkerSlots sized");
        var segment = segments[(uint)slot < (uint)segments.Length ? slot : 0];

        // One writer per slot is the design — each worker's TickContext carries its own view — but a view captured into a field or used from a Parallel.For
        // would put two threads on one segment. The gate makes that a wait, not a torn record; uncontended, it is one interlocked exchange.
        segment.Enter();
        try
        {
            var need = RecordHeader + info.PayloadSize;
            if (segment.Length + need > segment.Bytes.Length)
            {
                Array.Resize(ref segment.Bytes, Math.Max(segment.Bytes.Length * 2, segment.Length + need));
            }

            var span = segment.Bytes.AsSpan(segment.Length, need);
            MemoryMarshal.Write(span, info.Index);
            MemoryMarshal.Write(span[sizeof(int)..], target);
            MemoryMarshal.Write(span[RecordHeader..], in evt);
            segment.Length += need;
        }
        finally
        {
            segment.Exit();
        }
    }

    /// <summary>
    /// At a tick's start: a previous tick that no frame stage encoded — no session, no profile observing an archetype, an aborted tick — leaves its emissions
    /// behind; they are discarded, counted, rather than held and delivered later under another tick's number to sessions that were not there.
    /// </summary>
    /// <param name="tick">The tick starting.</param>
    public void OnTickStart(uint tick)
    {
        if (_lastEncoded + 1 == tick)
        {
            return;
        }

        foreach (var segment in _segments)
        {
            segment.Enter();
            try
            {
                if (segment.Length > 0)
                {
                    DiscardedBytes += segment.Length;
                    segment.Length = 0;
                }
            }
            finally
            {
                segment.Exit();
            }
        }
    }

    // ══ Encode (serial, frame prologue) ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Encodes the tick's emissions into its log slot and summary, then clears the segments. Serial, in the frame prologue, after projection (netIds and v̂
    /// exist), inside an epoch.
    /// </summary>
    /// <param name="tick">The tick.</param>
    /// <param name="entities">Resolves a live entity.</param>
    /// <param name="push">The push replication: this tick's departed entities, and the cell a point lies in.</param>
    public void EncodeTick<TEntities>(uint tick, ref TEntities entities, PushReplication push) where TEntities : IEventEntities, allows ref struct
    {
        var from = System.Diagnostics.Stopwatch.GetTimestamp();
        var slot = _log[tick % PushReplication.LogDepth];
        slot.Reset(tick);
        var any = false;
        foreach (var segment in _segments)
        {
            any |= segment.Length > 0;
        }

        if (any)
        {
            _departed.Clear();
            push.CollectDeparted(_departed);
            Array.Clear(_memo);
            Span<double> one = stackalloc double[4];
            Span<byte> pack = stackalloc byte[64];
            foreach (var segment in _segments)
            {
                segment.Enter();
                try
                {
                    var at = 0;
                    while (at + RecordHeader <= segment.Length)
                    {
                        var type = MemoryMarshal.Read<int>(segment.Bytes.AsSpan(at));
                        if ((uint)type >= (uint)_types.Length || at + RecordHeader + _types[type].PayloadSize > segment.Length)
                        {
                            Rejected++;
                            break;
                        }

                        var info = _types[type];
                        var target = MemoryMarshal.Read<ulong>(segment.Bytes.AsSpan(at + sizeof(int)));
                        var payload = segment.Bytes.AsSpan(at + RecordHeader, info.PayloadSize);
                        at += RecordHeader + info.PayloadSize;

                        // The routing point is the application's code: whatever it throws drops its event, not the tick.
                        try
                        {
                            if (Encode(slot, info, payload, ref entities, one, pack))
                            {
                                Route(slot, info, payload, target, ref entities, push);
                            }
                        }
                        catch (Exception)
                        {
                            Rejected++;
                        }
                    }
                }
                finally
                {
                    segment.Length = 0;
                    segment.Exit();
                }
            }
        }

        _lastEncoded = tick;

        slot.Finish();
        _summaries[tick % SummaryDepth].CopyRouting(slot);
        if (slot.Count > 0)
        {
            _lastNonEmpty = tick;
            Encoded += slot.Count;
        }

        EncodeTicks += System.Diagnostics.Stopwatch.GetTimestamp() - from;
    }

    // The last few entities resolved this tick: an event's entity fields and its routing name the same entities, and an EntityMap probe is most of an
    // event's cost. Cleared per tick — projection moves entities between ticks, not within the encode.
    private readonly Memo[] _memo = new Memo[4];
    private int _memoNext;

    private struct Memo
    {
        public ulong Entity;
        public bool Found;
        public uint NetId;
        public float X;
        public float Y;
        public float Z;
    }

    private bool Resolve<TEntities>(ref TEntities entities, EntityId entity, out uint netId, out float x, out float y, out float z)
        where TEntities : IEventEntities, allows ref struct
    {
        var raw = entity.RawValue;
        for (var i = 0; i < _memo.Length; i++)
        {
            ref var m = ref _memo[i];
            if (m.Entity == raw && raw != 0)
            {
                (netId, x, y, z) = (m.NetId, m.X, m.Y, m.Z);
                return m.Found;
            }
        }

        var found = entities.TryResolve(entity, out netId, out x, out y, out z);
        if (!found && _departed.TryGetValue((long)raw, out var gone))
        {
            (netId, x, y, z) = (gone.NetId, gone.X, gone.Y, gone.Z);
            found = true;
        }

        _memo[_memoNext] = new Memo { Entity = raw, Found = found, NetId = netId, X = x, Y = y, Z = z };
        _memoNext = (_memoNext + 1) & (_memo.Length - 1);
        return found;
    }

    private void Route<TEntities>(Tick slot, EventTypeInfo info, ReadOnlySpan<byte> payload, ulong target, ref TEntities entities, PushReplication push)
        where TEntities : IEventEntities, allows ref struct
    {
        var near = info.Routing == EventRouting.Near ? info.Point(payload) : default;
        var e = slot.Add();
        switch (info.Routing)
        {
            case EventRouting.Broadcast:
                slot.AddBroadcast(e);
                break;
            case EventRouting.ToOwner:
                slot.AddOwner(MemoryMarshal.Read<ulong>(payload[info.EntityOffsets[0]..]), e);
                break;
            case EventRouting.ToSession:
                slot.AddDirect(target, e);
                break;
            case EventRouting.Near:
            {
                push.CellOf(near.X, near.Y, near.Z, out var cx, out var cy, out var cz);
                slot.AddGeo(CellKey(cx, cy, cz), e, (float)near.X, (float)near.Y, (float)near.Z, info.NearRadius);
                break;
            }

            case EventRouting.ToKnown:
                foreach (var offset in info.EntityOffsets)
                {
                    var entity = EntityId.FromRaw((long)MemoryMarshal.Read<ulong>(payload[offset..]));
                    if (!entity.IsNull && Resolve(ref entities, entity, out _, out var x, out var y, out var z))
                    {
                        push.CellOf(x, y, z, out var cx, out var cy, out var cz);
                        slot.AddGeo(CellKey(cx, cy, cz), e, x, y, z, 0f);
                    }
                }

                break;
        }
    }

    private static ulong CellKey(int cx, int cy, int cz) => ((ulong)(uint)cz << 42) | ((ulong)(uint)cy << 21) | (uint)cx;

    private bool Encode<TEntities>(Tick slot, EventTypeInfo info, ReadOnlySpan<byte> payload, ref TEntities entities, scoped Span<double> one,
        scoped Span<byte> pack) where TEntities : IEventEntities, allows ref struct
    {
        slot.EnsureArena(info.MaxBytes);
        var w = new WireWriter(slot.Arena.AsSpan(slot.ArenaLength, info.MaxBytes));
        try
        {
            w.WriteVaru((uint)info.WireIdx);
            var body = info.Body;
            var bindings = info.Bindings;
            if (body.PackBytes > 0)
            {
                var bits = pack[..body.PackBytes];
                bits.Clear();
                for (var i = 0; i < body.PackedCount; i++)
                {
                    var f = bindings[i];
                    f.Load(payload, one);
                    FieldCodec.WritePackedBits(bits, f.Field.BitOffset, f.Field.BitCount, FieldCodec.PackedCode(f.Field, one[0]));
                }

                w.WriteBytes(bits);
            }

            for (var i = body.PackedCount; i < bindings.Length; i++)
            {
                var f = bindings[i];
                if (f.Entity)
                {
                    var entity = EntityId.FromRaw((long)MemoryMarshal.Read<ulong>(payload[f.Offset..]));
                    one[0] = !entity.IsNull && Resolve(ref entities, entity, out var netId, out _, out _, out _) ? netId : 0u;
                }
                else
                {
                    f.Load(payload, one);
                }

                FieldCodec.WriteNumber(ref w, f.Field, one);
            }
        }
        catch (ArgumentException)
        {
            Rejected++;
            return false;
        }

        slot.Commit(w.Position);
        return true;
    }

    // ══ Frames (parallel, read-only) ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Collects what a session's frame carries: the events routed to it in the ticks after <paramref name="lastTick"/> up to <paramref name="tick"/> — the
    /// current tick alone for a session with no frame yet — into <paramref name="picks"/>, and how many the log no longer holds.
    /// </summary>
    /// <param name="picks">The worker's scratch, filled for <see cref="Write"/>.</param>
    /// <param name="lastTick">The tick of the session's last committed frame; 0 for none.</param>
    /// <param name="tick">This tick.</param>
    /// <param name="controlled">The entity the session controls, for owner-routed events.</param>
    /// <param name="session">The session, for session-routed events.</param>
    /// <param name="geometry">The session's geometry, for the geometric routes.</param>
    /// <param name="count">Events to write, <c>EventsLost</c> included.</param>
    /// <param name="bytes">Their bytes, count and block header excluded.</param>
    /// <param name="lost">Events the log no longer holds.</param>
    public void Collect<TGeometry>(EventPicks picks, uint lastTick, uint tick, EntityId controlled, SessionId session, ref TGeometry geometry,
        out int count, out int bytes, out long lost) where TGeometry : IEventGeometry, allows ref struct
    {
        picks.Clear();
        count = 0;
        bytes = 0;
        lost = 0;
        var from = lastTick == 0 || lastTick >= tick ? tick : lastTick + 1;
        if (from > _lastNonEmpty)
        {
            return;
        }

        // Nothing older than the summary can be counted: a longer outage's count is a lower bound, and walking it would cost the gap.
        if (tick - from >= SummaryDepth)
        {
            from = tick - SummaryDepth + 1;
        }

        for (var t = from; t <= tick && t != 0; t++)
        {
            var slotIndex = (int)(t % PushReplication.LogDepth);
            var slot = _log[slotIndex];
            if (slot.Valid && slot.TickNumber == t)
            {
                slot.Match(picks, controlled.RawValue, session.Value, ref geometry);
                for (var i = 0; i < picks.TempCount; i++)
                {
                    var e = picks.Temp[i];
                    picks.Add(((long)slotIndex << 32) | (uint)e);
                    bytes += slot.Lengths[e];
                }

                count += picks.TempCount;
                continue;
            }

            var summary = _summaries[t % SummaryDepth];
            if (summary.Valid && summary.TickNumber == t)
            {
                summary.Match(picks, controlled.RawValue, session.Value, ref geometry);
                lost += picks.TempCount;
            }
        }

        if (lost > 0)
        {
            count++;
            bytes += 10;
        }
    }

    /// <summary>
    /// Folds a frame's collected events into its loss count: what a frame carries of events is bounded, so a burst can never make every frame oversize and
    /// starve the session's replication — a refused frame would only carry more at the next.
    /// </summary>
    public static void Shed(EventPicks picks, ref int count, ref int bytes, ref long lost)
    {
        lost += picks.Count;
        picks.Clear();
        count = lost > 0 ? 1 : 0;
        bytes = lost > 0 ? 10 : 0;
    }

    /// <summary>Writes the <c>EVENTS</c> block <see cref="Collect"/> described.</summary>
    public void Write(ref WireWriter w, EventPicks picks, int count, long lost)
    {
        var mark = TickWriter.BeginBlock(ref w, BlockTypes.Events);
        w.WriteVaru((uint)count);
        if (lost > 0)
        {
            w.WriteVaru(BuiltInEvents.EventsLostIdx);
            w.WriteVaru((uint)Math.Min(lost, uint.MaxValue));
        }

        for (var i = 0; i < picks.Count; i++)
        {
            var pick = picks.Items[i];
            var slot = _log[(int)(pick >> 32)];
            var e = (int)(uint)pick;
            w.WriteBytes(slot.Arena.AsSpan(slot.Starts[e], slot.Lengths[e]));
        }

        TickWriter.EndBlock(ref w, mark);
    }

    /// <summary>Counts a frame's delivery: <paramref name="count"/> events, <paramref name="lost"/> of them folded into <c>EventsLost</c>.</summary>
    public void NoteDelivered(int count, long lost)
    {
        System.Threading.Interlocked.Add(ref Delivered, count);
        if (lost > 0)
        {
            System.Threading.Interlocked.Add(ref Lost, lost);
        }
    }

    // ══ Storage ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A worker slot's emissions. Padded past a cache line: workers emitting into neighbouring segments must not share one.</summary>
    private sealed class Segment
    {
        public byte[] Bytes = new byte[4096];
        public int Length;
        private int _gate;
#pragma warning disable CS0169, IDE0051 // padding
        private long _p0, _p1, _p2, _p3, _p4, _p5, _p6;
#pragma warning restore CS0169, IDE0051

        public void Enter()
        {
            while (System.Threading.Interlocked.CompareExchange(ref _gate, 1, 0) != 0)
            {
                System.Threading.Thread.SpinWait(8);
            }
        }

        public void Exit() => System.Threading.Volatile.Write(ref _gate, 0);
    }

    /// <summary>A geometric route's point: the event, the cell, the point and the event's viewpoint radius.</summary>
    private struct Geo
    {
        public ulong Key;
        public int Event;
        public float X;
        public float Y;
        public float Z;
        public float Radius;
    }

    /// <summary>
    /// One tick of the event log: the encoded arena, each event's span, and its routing — the broadcast list, the owner and session lists sorted by key, the
    /// geometric entries sorted by cell. A summary slot keeps the routing and no bytes.
    /// </summary>
    private sealed class Tick
    {
        public uint TickNumber;
        public bool Valid;
        public byte[] Arena = new byte[4096];
        public int ArenaLength;
        public int[] Starts = new int[64];
        public int[] Lengths = new int[64];
        public int Count;
        public int[] Broadcast = new int[64];
        public int BroadcastCount;
        public ulong[] OwnerKeys = new ulong[16];
        public int[] OwnerEvents = new int[16];
        public int OwnerCount;
        public ulong[] DirectKeys = new ulong[16];
        public int[] DirectEvents = new int[16];
        public int DirectCount;
        public Geo[] Geos = new Geo[16];
        public int GeoCount;
        private int _pendingStart;

        public void Reset(uint tick)
        {
            TickNumber = tick;
            Valid = true;
            ArenaLength = 0;
            Count = 0;
            BroadcastCount = 0;
            OwnerCount = 0;
            DirectCount = 0;
            GeoCount = 0;
        }

        public void EnsureArena(int bytes)
        {
            if (ArenaLength + bytes > Arena.Length)
            {
                Array.Resize(ref Arena, Math.Max(Arena.Length * 2, ArenaLength + bytes));
            }

            _pendingStart = ArenaLength;
        }

        public void Commit(int length) => ArenaLength = _pendingStart + length;

        /// <summary>The event just committed; returns its index.</summary>
        public int Add()
        {
            if (Count == Starts.Length)
            {
                Array.Resize(ref Starts, Count * 2);
                Array.Resize(ref Lengths, Count * 2);
            }

            Starts[Count] = _pendingStart;
            Lengths[Count] = ArenaLength - _pendingStart;
            return Count++;
        }

        public void AddBroadcast(int e)
        {
            if (BroadcastCount == Broadcast.Length)
            {
                Array.Resize(ref Broadcast, BroadcastCount * 2);
            }

            Broadcast[BroadcastCount++] = e;
        }

        public void AddOwner(ulong key, int e) => Append(ref OwnerKeys, ref OwnerEvents, ref OwnerCount, key, e);

        public void AddDirect(ulong key, int e) => Append(ref DirectKeys, ref DirectEvents, ref DirectCount, key, e);

        public void AddGeo(ulong key, int e, float x, float y, float z, float radius)
        {
            if (GeoCount == Geos.Length)
            {
                Array.Resize(ref Geos, GeoCount * 2);
            }

            Geos[GeoCount++] = new Geo { Key = key, Event = e, X = x, Y = y, Z = z, Radius = radius };
        }

        private static void Append(ref ulong[] keys, ref int[] events, ref int count, ulong key, int e)
        {
            if (count == keys.Length)
            {
                Array.Resize(ref keys, count * 2);
                Array.Resize(ref events, count * 2);
            }

            keys[count] = key;
            events[count++] = e;
        }

        /// <summary>Sorts the keyed lists by key, then emission order, and the geometric entries by cell.</summary>
        public void Finish()
        {
            SortKeyed(OwnerKeys, OwnerEvents, OwnerCount);
            SortKeyed(DirectKeys, DirectEvents, DirectCount);
            if (GeoCount > 1)
            {
                Geos.AsSpan(0, GeoCount).Sort(static (a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : a.Event.CompareTo(b.Event));
            }
        }

        private static void SortKeyed(ulong[] keys, int[] events, int count)
        {
            if (count < 2)
            {
                return;
            }

            Array.Sort(keys, events, 0, count);

            // Array.Sort is not stable: restore emission order within each key's run.
            for (var i = 0; i < count;)
            {
                var j = i + 1;
                while (j < count && keys[j] == keys[i])
                {
                    j++;
                }

                if (j - i > 1)
                {
                    Array.Sort(events, i, j - i);
                }

                i = j;
            }
        }

        /// <summary>Copies another slot's routing, without its bytes: the summary of a tick the log will drop.</summary>
        public void CopyRouting(Tick from)
        {
            TickNumber = from.TickNumber;
            Valid = true;
            Count = from.Count;
            BroadcastCount = from.BroadcastCount;
            OwnerCount = from.OwnerCount;
            DirectCount = from.DirectCount;
            GeoCount = from.GeoCount;
            Copy(from.Broadcast, ref Broadcast, BroadcastCount);
            Copy(from.OwnerKeys, ref OwnerKeys, OwnerCount);
            Copy(from.OwnerEvents, ref OwnerEvents, OwnerCount);
            Copy(from.DirectKeys, ref DirectKeys, DirectCount);
            Copy(from.DirectEvents, ref DirectEvents, DirectCount);
            Copy(from.Geos, ref Geos, GeoCount);
        }

        private static void Copy<T>(T[] source, ref T[] target, int count)
        {
            if (target.Length < count)
            {
                target = new T[Math.Max(count, target.Length * 2)];
            }

            Array.Copy(source, target, count);
        }

        /// <summary>A session's matches in this tick, deduplicated, in emission order, into <paramref name="picks"/>' temp list.</summary>
        public void Match<TGeometry>(EventPicks picks, ulong controlled, ulong session, ref TGeometry geometry) where TGeometry : IEventGeometry, allows ref struct
        {
            picks.TempCount = 0;
            for (var i = 0; i < BroadcastCount; i++)
            {
                picks.AddTemp(Broadcast[i]);
            }

            var sources = BroadcastCount > 0 ? 1 : 0;
            var before = picks.TempCount;
            AddRun(picks, OwnerKeys, OwnerEvents, OwnerCount, controlled);
            sources += picks.TempCount > before ? 1 : 0;
            before = picks.TempCount;
            AddRun(picks, DirectKeys, DirectEvents, DirectCount, session);
            sources += picks.TempCount > before ? 1 : 0;
            before = picks.TempCount;

            if (GeoCount > 0)
            {
                if (geometry.World)
                {
                    for (var i = 0; i < GeoCount; i++)
                    {
                        ref var g = ref Geos[i];
                        if (geometry.Sees(g.X, g.Y, g.Z, g.Radius))
                        {
                            picks.AddTemp(g.Event);
                        }
                    }
                }
                else
                {
                    geometry.CellBox(out var minCx, out var maxCx, out var minCy, out var maxCy, out var minCz, out var maxCz);

                    // No entry in the box's key range: none in any of its rows.
                    if (CellKey(maxCx, maxCy, maxCz) < Geos[0].Key || CellKey(minCx, minCy, minCz) > Geos[GeoCount - 1].Key)
                    {
                        minCz = maxCz + 1;
                    }

                    for (var cz = minCz; cz <= maxCz; cz++)
                    {
                        for (var cy = minCy; cy <= maxCy; cy++)
                        {
                            var hi = CellKey(maxCx, cy, cz);
                            for (var i = LowerBound(CellKey(minCx, cy, cz)); i < GeoCount && Geos[i].Key <= hi; i++)
                            {
                                ref var g = ref Geos[i];
                                if (geometry.Sees(g.X, g.Y, g.Z, g.Radius))
                                {
                                    picks.AddTemp(g.Event);
                                }
                            }
                        }
                    }
                }
            }

            // Each source's matches are in emission order but for the geometric ones (in cell order); several sources interleave, and an event with two
            // points the session sees counts once — then they are sorted and deduplicated.
            var geo = picks.TempCount > before;
            if (picks.TempCount > 1 && (geo || sources > 1))
            {
                Array.Sort(picks.Temp, 0, picks.TempCount);
                var n = 1;
                for (var i = 1; i < picks.TempCount; i++)
                {
                    if (picks.Temp[i] != picks.Temp[n - 1])
                    {
                        picks.Temp[n++] = picks.Temp[i];
                    }
                }

                picks.TempCount = n;
            }
        }

        private static void AddRun(EventPicks picks, ulong[] keys, int[] events, int count, ulong key)
        {
            if (key == 0 || count == 0)
            {
                return;
            }

            for (var i = LowerBound(keys, count, key); i < count && keys[i] == key; i++)
            {
                picks.AddTemp(events[i]);
            }
        }

        private int LowerBound(ulong key)
        {
            var lo = 0;
            var hi = GeoCount;
            while (lo < hi)
            {
                var mid = (lo + hi) >>> 1;
                if (Geos[mid].Key < key)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            return lo;
        }

        private static int LowerBound(ulong[] keys, int count, ulong key)
        {
            var lo = 0;
            var hi = count;
            while (lo < hi)
            {
                var mid = (lo + hi) >>> 1;
                if (keys[mid] < key)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            return lo;
        }
    }
}
