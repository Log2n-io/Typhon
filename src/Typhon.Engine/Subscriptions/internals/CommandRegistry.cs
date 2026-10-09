using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Typhon.Protocol;

namespace Typhon.Engine.Internals;

/// <summary>How a decoded wire number is stored into a command struct's field.</summary>
internal enum CommandFieldElement
{
    /// <summary>Signed 8-bit.</summary>
    I8,

    /// <summary>Unsigned 8-bit.</summary>
    U8,

    /// <summary>Signed 16-bit.</summary>
    I16,

    /// <summary>Unsigned 16-bit.</summary>
    U16,

    /// <summary>Signed 32-bit.</summary>
    I32,

    /// <summary>Unsigned 32-bit.</summary>
    U32,

    /// <summary>Signed 64-bit.</summary>
    I64,

    /// <summary>Unsigned 64-bit.</summary>
    U64,

    /// <summary>Single-precision.</summary>
    F32,

    /// <summary>Double-precision.</summary>
    F64,
}

/// <summary>
/// Where one wire field of a command lands inside the command struct, resolved once at <c>Start</c>.
/// </summary>
/// <remarks>
/// The wire order is the catalog's canonical order and the struct's order is the declaration's; the two are bound by NAME, here, so reordering a struct's
/// members never changes the wire and never silently moves a value into the neighbouring field (01-model § 7).
/// </remarks>
internal readonly struct CommandFieldBinding
{
    internal CommandFieldBinding(string wireName, int offset, int components, CommandFieldElement element, int elementSize, int textCapacity = 0)
    {
        WireName = wireName;
        Offset = offset;
        Components = components;
        Element = element;
        ElementSize = elementSize;
        TextCapacity = textCapacity;
        ElementRange = element switch
        {
            CommandFieldElement.I8 => new IntegerRange(sbyte.MinValue, (ulong)sbyte.MaxValue),
            CommandFieldElement.U8 => new IntegerRange(0, byte.MaxValue),
            CommandFieldElement.I16 => new IntegerRange(short.MinValue, (ulong)short.MaxValue),
            CommandFieldElement.U16 => new IntegerRange(0, ushort.MaxValue),
            CommandFieldElement.I32 => new IntegerRange(int.MinValue, int.MaxValue),
            CommandFieldElement.U32 => new IntegerRange(0, uint.MaxValue),
            CommandFieldElement.I64 => new IntegerRange(long.MinValue, long.MaxValue),
            CommandFieldElement.U64 => new IntegerRange(0, ulong.MaxValue),
            _ => default,
        };
    }

    /// <summary>The field's wire name.</summary>
    public string WireName { get; }

    /// <summary>Byte offset of the struct field.</summary>
    public int Offset { get; }

    /// <summary>Numbers the codec produces for this field: 1 for a scalar, 2 or 3 for a vector.</summary>
    public int Components { get; }

    /// <summary>How each number is stored.</summary>
    public CommandFieldElement Element { get; }

    /// <summary>Bytes each stored number occupies.</summary>
    public int ElementSize { get; }

    /// <summary>The range an integer element holds, which a 64-bit wire value clamps to; 0..0 for a float element.</summary>
    public IntegerRange ElementRange { get; }

    /// <summary>An inline <c>Utf8Text</c>'s capacity, or 0 when the field is not text.</summary>
    public int TextCapacity { get; }

    /// <summary>Whether this field carries text rather than numbers.</summary>
    public bool IsText => TextCapacity > 0;

    /// <summary>
    /// Each component's byte offset from <see cref="Offset"/>, for a fixed shape (W33) whose wire order is its members' names, not their places; null
    /// for a field whose components are contiguous in order.
    /// </summary>
    public int[] ComponentOffsets { get; init; }

    private int ComponentOffset(int i) => ComponentOffsets?[i] ?? i * ElementSize;

    /// <summary>
    /// Stores decoded UTF-8 into an inline text field: a <see cref="ushort"/> length, then the bytes.
    /// </summary>
    /// <param name="payload">The command struct's bytes.</param>
    /// <param name="utf8">The decoded bytes, already length-capped and UTF-8-validated by the wire reader.</param>
    /// <remarks>
    /// The reader caps the length at the field's declared <c>maxBytes</c> before this is reached, and the binder refused a
    /// capacity that disagrees with it — so a longer value is a decode that should already have failed. It is truncated
    /// here rather than trusted, because the alternative is writing past the field into its neighbour.
    /// </remarks>
    public void StoreText(Span<byte> payload, scoped ReadOnlySpan<byte> utf8)
    {
        var length = Math.Min(utf8.Length, TextCapacity);
        MemoryMarshal.Write(payload[Offset..], (ushort)length);
        utf8[..length].CopyTo(payload.Slice(Offset + sizeof(ushort), length));
    }

    /// <summary>Writes <paramref name="components"/> into <paramref name="payload"/> at this binding's offset.</summary>
    /// <param name="payload">The command struct's bytes.</param>
    /// <param name="components">The decoded numbers.</param>
    public void Store(Span<byte> payload, scoped ReadOnlySpan<double> components)
    {
        var integral = Element is not (CommandFieldElement.F32 or CommandFieldElement.F64);
        for (var i = 0; i < Components; i++)
        {
            var slot = payload.Slice(Offset + ComponentOffset(i), ElementSize);
            var value = components[i];

            // An integer field may travel as a quant (13 § 2.3), which decodes to a value between integers: rounded half away from zero (W1's rha), never
            // truncated. Every integer codec decodes to a whole number, which rounding leaves as it is.
            if (integral)
            {
                value = WireMath.RoundHalfAwayFromZero(value);
            }

            switch (Element)
            {
                case CommandFieldElement.I8:
                    slot[0] = unchecked((byte)(sbyte)Math.Clamp(value, sbyte.MinValue, sbyte.MaxValue));
                    break;
                case CommandFieldElement.U8:
                    slot[0] = (byte)Math.Clamp(value, byte.MinValue, byte.MaxValue);
                    break;
                case CommandFieldElement.I16:
                    MemoryMarshal.Write(slot, (short)Math.Clamp(value, short.MinValue, short.MaxValue));
                    break;
                case CommandFieldElement.U16:
                    MemoryMarshal.Write(slot, (ushort)Math.Clamp(value, ushort.MinValue, ushort.MaxValue));
                    break;
                case CommandFieldElement.I32:
                    MemoryMarshal.Write(slot, (int)Math.Clamp(value, int.MinValue, int.MaxValue));
                    break;
                case CommandFieldElement.U32:
                    MemoryMarshal.Write(slot, (uint)Math.Clamp(value, uint.MinValue, uint.MaxValue));
                    break;
                case CommandFieldElement.I64:
                    MemoryMarshal.Write(slot, (long)Math.Clamp(value, long.MinValue, long.MaxValue));
                    break;
                case CommandFieldElement.U64:
                    MemoryMarshal.Write(slot, (ulong)Math.Clamp(value, ulong.MinValue, ulong.MaxValue));
                    break;
                case CommandFieldElement.F32:
                    MemoryMarshal.Write(slot, (float)value);
                    break;
                default:
                    MemoryMarshal.Write(slot, value);
                    break;
            }
        }
    }

    /// <summary>
    /// Writes 64-bit integer <paramref name="components"/> (W32) into <paramref name="payload"/> at this binding's offset, exactly — never through a double.
    /// A struct field narrower than the wire value clamps to its own range, as <see cref="Store"/> does.
    /// </summary>
    /// <param name="payload">The command struct's bytes.</param>
    /// <param name="components">The decoded bit patterns.</param>
    /// <param name="signedWire">Whether the codec is signed (<c>i64</c>, <c>vari64</c>): the patterns are two's complement.</param>
    public void StoreInteger64(Span<byte> payload, scoped ReadOnlySpan<ulong> components, bool signedWire)
    {
        for (var i = 0; i < Components; i++)
        {
            var slot = payload.Slice(Offset + ComponentOffset(i), ElementSize);
            var bits = components[i];
            var range = ElementRange;

            // Clamped to the element's range as a two's-complement pattern: a signed wire value against both bounds, an unsigned one against the top
            // alone (a range contains 0). The element then keeps the pattern's low bytes, which is the clamped value whatever its signedness.
            if (signedWire)
            {
                var value = unchecked((long)bits);
                bits = unchecked((ulong)(value < range.Min ? range.Min : value > range.SignedMax ? range.SignedMax : value));
            }
            else if (bits > range.Max)
            {
                bits = range.Max;
            }

            switch (Element)
            {
                case CommandFieldElement.I8:
                case CommandFieldElement.U8:
                    slot[0] = unchecked((byte)bits);
                    break;
                case CommandFieldElement.I16:
                case CommandFieldElement.U16:
                    MemoryMarshal.Write(slot, unchecked((ushort)bits));
                    break;
                case CommandFieldElement.I32:
                case CommandFieldElement.U32:
                    MemoryMarshal.Write(slot, unchecked((uint)bits));
                    break;
                case CommandFieldElement.I64:
                case CommandFieldElement.U64:
                    MemoryMarshal.Write(slot, bits);
                    break;
                default:
                    // A float field never pairs with a 64-bit integer codec (13 § 2.3); the registry refused it at Start.
                    throw new InvalidOperationException($"Command field '{WireName}' stores a 64-bit integer into a {Element} field.");
            }
        }
    }
}

/// <summary>
/// One command type as the ingress path needs it: its wire index and plan, its delivery policy, its rate bucket parameters, and where each wire field lands
/// in the application's struct.
/// </summary>
internal sealed class CommandTypeInfo
{
    internal CommandTypeInfo(int wireIdx, MessagePlan plan, Type structType, int payloadSize, CommandCoalesce coalesce, uint rolesMask, int ratePerSecond,
        int rateBurst, CommandFieldBinding[] bindings, Delegate precheck, CommandPrecheckAdapter precheckAdapter, string[] unboundStructFields,
        bool isClientRegion)
    {
        WireIdx = wireIdx;
        Plan = plan;
        StructType = structType;
        PayloadSize = payloadSize;
        Coalesce = coalesce;
        RolesMask = rolesMask;
        RatePerSecond = ratePerSecond;
        RateBurst = rateBurst;
        Bindings = bindings;
        Precheck = precheck;
        PrecheckAdapter = precheckAdapter;
        UnboundStructFields = unboundStructFields;
        IsClientRegion = isClientRegion;
    }

    /// <summary>The catalog index a client names this command by.</summary>
    public int WireIdx { get; }

    /// <summary>The compiled body, in wire order.</summary>
    public MessagePlan Plan { get; }

    /// <summary>The application's struct, or <see langword="null"/> for the built-in region the engine interprets itself.</summary>
    public Type StructType { get; }

    /// <summary>Bytes one decoded command occupies in the ring and in the tick's buffers. Variable for the region, which carries only the hull it has.</summary>
    public int PayloadSize { get; }

    /// <summary>Queued, or newest-per-session.</summary>
    public CommandCoalesce Coalesce { get; }

    /// <summary>A bit per allowed <see cref="SessionRole"/>; zero accepts every role.</summary>
    public uint RolesMask { get; }

    /// <summary>The per-session token bucket's sustained rate; zero means unlimited.</summary>
    public int RatePerSecond { get; }

    /// <summary>The per-session token bucket's depth.</summary>
    public int RateBurst { get; }

    /// <summary>One binding per wire field, parallel to <see cref="MessagePlan.Body"/>'s fields. Empty for the region.</summary>
    public CommandFieldBinding[] Bindings { get; }

    /// <summary>The declaration's syntactic pre-check, or <see langword="null"/>.</summary>
    public Delegate Precheck { get; }

    /// <summary>Calls <see cref="Precheck"/> against raw payload bytes without knowing the struct type. <see langword="null"/> when there is none.</summary>
    public CommandPrecheckAdapter PrecheckAdapter { get; }

    /// <summary>
    /// Struct fields the catalog does not carry, in declaration order. They are never written and read as their default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The catalog is the wire, and it is the whole wire.</b> This array is read off the catalog the client negotiated against, never off the declarations:
    /// inventing a wire field here for a member the catalog does not name would make the engine's own decode disagree with every client, which is a protocol
    /// break dressed as a convenience.
    /// </para>
    /// <para>
    /// <b>It is now a report on <c>Ignore</c>, and normally empty.</b> A field defaults to its raw type (01-model § 7), so the only way a member stays out of
    /// the catalog is a declaration saying <see cref="CommandBuilder{T}.Ignore{TField}"/> — which makes what remains here a list of decisions rather than the
    /// list of omissions it used to be. It is kept because that is exactly what an operator wants to read: the struct members the wire never fills.
    /// </para>
    /// </remarks>
    public string[] UnboundStructFields { get; }

    /// <summary>Whether this is the built-in <c>ClientRegion</c>, which the engine decodes into its own region rather than into an application struct.</summary>
    public bool IsClientRegion { get; }

    /// <summary>Whether <paramref name="role"/> may send this command.</summary>
    /// <param name="role">The session's role.</param>
    /// <returns><see langword="true"/> when the declaration allows it.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowsRole(SessionRole role) => RolesMask == 0 || (RolesMask & (1u << (int)role)) != 0;
}

/// <summary>Calls a typed <see cref="CommandPrecheck{T}"/> against raw payload bytes.</summary>
/// <param name="precheck">The declaration's delegate.</param>
/// <param name="payload">The decoded command struct's bytes.</param>
/// <returns>What the pre-check answered.</returns>
/// <remarks>
/// Built by <see cref="CommandBuilder{T}"/>, where <c>T</c> is a compile-time generic argument, so no <c>MakeGenericType</c> or <c>Activator</c> is needed to
/// reach a typed delegate from a <see cref="Type"/> — the AOT blocker class #409 names. The lambda behind it is <c>static</c>, so it is one cached instance
/// per command type rather than an allocation per call.
/// </remarks>
internal delegate bool CommandPrecheckAdapter(Delegate precheck, ReadOnlySpan<byte> payload);

/// <summary>
/// Every command type a client may send, indexed by the wire index the catalog gave it, with the binding from wire fields to struct offsets resolved once.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is built from the catalog, not from the declarations.</b> The catalog is what a client negotiated against, so the decode has to follow it field for
/// field; the declarations are consulted only for what never reaches the wire — delivery mode, roles, rate and the pre-check. Building the decode from the
/// declarations instead would be the second parser 03 § 8 forbids.
/// </para>
/// <para>
/// <b>Read-only after <c>Start</c>.</b> Transport threads read it on every <c>COMMANDS</c> message and the drain reads it every tick; nothing mutates it, so
/// neither side needs any synchronization to reach it.
/// </para>
/// </remarks>
internal sealed class CommandRegistry
{
    /// <summary>The largest command struct ingress will decode. A bigger one would not fit the stack buffer the transport thread decodes into.</summary>
    internal const int MaxPayloadBytes = 512;

    private readonly CommandTypeInfo[] _byWireIdx;
    private readonly Dictionary<Type, CommandTypeInfo> _byStruct;

    private CommandRegistry(CommandTypeInfo[] byWireIdx, Dictionary<Type, CommandTypeInfo> byStruct, int count, int maxPayloadBytes, CatalogPlan plan)
    {
        _byWireIdx = byWireIdx;
        _byStruct = byStruct;
        Count = count;
        MaxDecodedPayloadBytes = maxPayloadBytes;
        Plan = plan;
    }

    /// <summary>The compiled catalog every decode runs against.</summary>
    public CatalogPlan Plan { get; }

    /// <summary>How many command types exist.</summary>
    public int Count { get; }

    /// <summary>One past the highest wire index, which is how wide a per-type table has to be.</summary>
    public int WireIdxCount => _byWireIdx.Length;

    /// <summary>The largest payload a decoded command occupies, so a caller can size one buffer for every type.</summary>
    public int MaxDecodedPayloadBytes { get; }

    /// <summary>The command at a wire index, or <see langword="null"/> when the catalog has none there.</summary>
    /// <param name="wireIdx">The index.</param>
    /// <returns>The command type.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public CommandTypeInfo ByWireIdx(int wireIdx) => (uint)wireIdx < (uint)_byWireIdx.Length ? _byWireIdx[wireIdx] : null;

    /// <summary>The command declared with a given struct, or <see langword="null"/>.</summary>
    /// <param name="structType">The application's command struct.</param>
    /// <returns>The command type.</returns>
    public CommandTypeInfo ByStruct(Type structType) => _byStruct.TryGetValue(structType, out var info) ? info : null;

    /// <summary>
    /// Binds the catalog's commands to the application's declarations and structs.
    /// </summary>
    /// <param name="registry">The frozen declarations.</param>
    /// <param name="plan">The compiled catalog.</param>
    /// <returns>The registry, empty when the catalog declares no command.</returns>
    /// <exception cref="InvalidOperationException">A declared command's struct cannot carry one of its wire fields.</exception>
    public static CommandRegistry Build(SubscriptionsRegistry registry, CatalogPlan plan)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(plan);

        var commands = plan.Catalog.Commands ?? [];
        var highest = -1;
        foreach (var command in commands)
        {
            highest = Math.Max(highest, command.Idx);
        }

        // The ingress ring marks a realm-framed command in its type index's top bit (SubscriptionsIngress.FramedRecordFlag).
        if (highest >= SubscriptionsIngress.FramedRecordFlag)
        {
            throw new InvalidOperationException($"The catalog declares command index {highest}: at most {SubscriptionsIngress.FramedRecordFlag - 1} are carried.");
        }

        var table = new CommandTypeInfo[highest + 1];
        var byStruct = new Dictionary<Type, CommandTypeInfo>();
        var count = 0;
        var maxPayload = 0;

        foreach (var command in commands)
        {
            var messagePlan = plan.Command(command.Idx);
            var info = command.Name == BuiltInCommands.ClientRegion
                ? BuildClientRegion(messagePlan, command)
                : BuildApplicationCommand(registry, messagePlan);

            table[command.Idx] = info;
            if (info.StructType != null)
            {
                byStruct[info.StructType] = info;
            }

            maxPayload = Math.Max(maxPayload, info.PayloadSize);
            count++;
        }

        return new CommandRegistry(table, byStruct, count, maxPayload, plan);
    }

    private static CommandTypeInfo BuildClientRegion(MessagePlan plan, CatalogCommand command)
    {
        // The rate comes from the catalog rather than from a constant here: the catalog is what the client was told, and a second spelling of 5/s would be one
        // more place for the two to drift. The region's decoder likewise reads its three fields by NAME, because canonicalization fixes their order and this
        // code must not become a second opinion about what that order is.
        var rate = command.Rate?.PerSec ?? 0;
        var burst = command.Rate?.Burst ?? 0;
        return new CommandTypeInfo(plan.Idx, plan, null, ClientRegionCommand.MaxRecordBytes, CommandCoalesce.LatestPerSession, rolesMask: 0,
            ratePerSecond: rate, rateBurst: burst, bindings: [], precheck: null, precheckAdapter: null, unboundStructFields: [], isClientRegion: true);
    }

    private static CommandTypeInfo BuildApplicationCommand(SubscriptionsRegistry registry, MessagePlan plan)
    {
        CommandDeclaration declaration = null;
        foreach (var candidate in registry.Commands)
        {
            if (string.Equals(candidate.Name, plan.Name, StringComparison.Ordinal))
            {
                declaration = candidate;
                break;
            }
        }

        if (declaration == null)
        {
            throw new InvalidOperationException(
                $"The catalog carries a command '{plan.Name}' that no declaration produced. Commands reach the catalog only through " +
                $"{nameof(SubscriptionsRegistry)}.{nameof(SubscriptionsRegistry.Command)} and the engine's own built-ins.");
        }

        var structType = declaration.CommandType;
        RefuseUnsuitableStruct(structType, plan.Name);

        var payloadSize = Marshal.SizeOf(structType);
        if (payloadSize > MaxPayloadBytes)
        {
            throw new InvalidOperationException(
                $"Command '{plan.Name}' is {payloadSize} bytes and {MaxPayloadBytes} is the ceiling. A command is one client's intent for one frame, decoded " +
                "into a stack buffer on a transport thread; anything this large is state, and state belongs in the database.");
        }

        var fields = plan.Body.Fields;
        var bindings = new CommandFieldBinding[fields.Length];
        var bound = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < fields.Length; i++)
        {
            bindings[i] = BindField(declaration, structType, fields[i], bound);
        }

        var rolesMask = 0u;
        foreach (var role in declaration.AllowedRoles)
        {
            rolesMask |= 1u << (int)role;
        }

        return new CommandTypeInfo(plan.Idx, plan, structType, payloadSize, declaration.Coalesce, rolesMask, declaration.RatePerSecond, declaration.RateBurst,
            bindings, declaration.Precheck, declaration.PrecheckAdapter, UnboundFields(structType, bound), isClientRegion: false);
    }

    private static CommandFieldBinding BindField(CommandDeclaration declaration, Type structType, FieldPlan field, HashSet<string> bound)
    {
        if (field.ValueKind is not (FieldValueKind.Number or FieldValueKind.Integer64 or FieldValueKind.Text))
        {
            throw new InvalidOperationException(
                $"Command '{declaration.Name}' field '{field.Name}' travels as {field.ValueKind}, and a command decodes " +
                "into an unmanaged struct: it carries " +
                "numbers and an inline Utf8Text, but no blob and no list. Carry the value as a number, or send it as state rather than as a command.");
        }

        // The declaration is what names the struct member: the wire name may have been overridden, and it is the SOURCE name that has to resolve.
        var sourceName = field.Name;
        foreach (var declared in declaration.Fields)
        {
            if (string.Equals(declared.Name, field.Name, StringComparison.Ordinal))
            {
                sourceName = declared.SourceFieldName;
                break;
            }
        }

        var member = structType.GetField(sourceName, BindingFlags.Public | BindingFlags.Instance);
        if (member == null)
        {
            throw new InvalidOperationException(
                $"Command '{declaration.Name}' carries the wire field '{field.Name}', and '{structType.Name}' has no public instance field '{sourceName}' to " +
                "put it in.");
        }

        if (field.ValueKind == FieldValueKind.Text)
        {
            var capacity = MessageText.CapacityOf(member.FieldType)
                           ?? throw new InvalidOperationException(
                               $"Command '{declaration.Name}' field '{field.Name}' travels as a str, and " +
                               $"'{structType.Name}.{sourceName}' is a '{member.FieldType.Name}'. A str field is an inline " +
                               $"text type — {MessageText.KnownTypes} — because a command struct owns its bytes.");
            // Only a capacity SMALLER than the wire cap is unsafe — then the wire admits more bytes than the struct can
            // hold and the surplus is truncated silently. A capacity LARGER is strictly safe: the wire cap is the tighter
            // of the two and nothing is lost. Demanding equality made one storage size the engine's ingress policy, since
            // a 24-byte name field then had to admit 256 bytes from every client on every message — and the wire design
            // names per-field `maxBytes` as exactly that knob.
            if (capacity < field.Codec.MaxBytes)
            {
                throw new InvalidOperationException(
                    $"Command '{declaration.Name}' field '{field.Name}' declares Codec.Str({field.Codec.MaxBytes}) and " +
                    $"'{structType.Name}.{sourceName}' holds only {capacity}. The struct must be able to carry the cap the " +
                    "wire enforces, or the surplus is truncated without a word.");
            }

            if (!bound.Add(sourceName))
            {
                throw new InvalidOperationException(
                    $"Command '{declaration.Name}' binds '{structType.Name}.{sourceName}' twice; two wire fields cannot share one struct field.");
            }

            return new CommandFieldBinding(
                field.Name, (int)Marshal.OffsetOf(structType, sourceName), 1, CommandFieldElement.U8, sizeof(byte), capacity);
        }

        var (element, elementSize) = ElementOf(member.FieldType, declaration.Name, field.Name, field.Components);
        if (field.ValueKind == FieldValueKind.Integer64 && element is CommandFieldElement.F32 or CommandFieldElement.F64)
        {
            throw new InvalidOperationException(
                $"Command '{declaration.Name}' field '{field.Name}' travels as {field.Codec.Type}, a 64-bit integer, and '{structType.Name}.{sourceName}' is " +
                $"a '{member.FieldType.Name}'. A 64-bit integer lands in an integer field.");
        }

        // Unwrapped exactly as ElementOf unwraps it, because Marshal.SizeOf refuses an enum TYPE outright — "no meaningful size or offset can be computed" —
        // even though the same enum marshals perfectly well as a member of the struct around it. Measuring the underlying type is the size the field occupies.
        var fieldSize = Marshal.SizeOf(member.FieldType.IsEnum ? Enum.GetUnderlyingType(member.FieldType) : member.FieldType);
        if (fieldSize != field.Components * elementSize)
        {
            throw new InvalidOperationException(
                $"Command '{declaration.Name}' field '{field.Name}' decodes to {field.Components} number(s) of {elementSize} bytes, and " +
                $"'{structType.Name}.{sourceName}' is {fieldSize} bytes. A vector codec needs a field of exactly that many contiguous components.");
        }

        if (!bound.Add(sourceName))
        {
            throw new InvalidOperationException(
                $"Command '{declaration.Name}' binds '{structType.Name}.{sourceName}' twice; two wire fields cannot share one struct field.");
        }

        var offset = (int)Marshal.OffsetOf(structType, sourceName);
        return new CommandFieldBinding(field.Name, offset, field.Components, element, elementSize)
        {
            ComponentOffsets = FieldShape.Of(member.FieldType)?.Offsets,
        };
    }

    /// <summary>The element kind of a struct field, unwrapping an enum and a single-component vector struct alike.</summary>
    /// <param name="fieldType">The struct field's type.</param>
    /// <param name="command">The command, for a refusal.</param>
    /// <param name="wireField">The wire field, for a refusal.</param>
    /// <param name="components">The values the codec decodes to, which a multi-component struct's size is divided by.</param>
    internal static (CommandFieldElement Element, int Size) ElementOf(Type fieldType, string command, string wireField, int components = 1)
    {
        // A client names an entity by the netId it holds, never by the server's EntityId: read as a number, its eight bytes would be a double's bit
        // pattern written into an identity (an event resolves its references before it gets here).
        if (CodecPairing.IsReference(fieldType))
        {
            throw new InvalidOperationException(
                $"Command '{command}' field '{wireField}' reads a '{fieldType.Name}'. A client names an entity by the netId it holds: declare the field a " +
                "uint with Codec.EntityRef and resolve it with Subscriptions.Commands.TryResolve (01 § 7).");
        }

        var type = fieldType.IsEnum ? Enum.GetUnderlyingType(fieldType) : fieldType;

        if (type == typeof(sbyte))
        {
            return (CommandFieldElement.I8, 1);
        }

        if (type == typeof(byte))
        {
            return (CommandFieldElement.U8, 1);
        }

        if (type == typeof(short))
        {
            return (CommandFieldElement.I16, 2);
        }

        if (type == typeof(ushort))
        {
            return (CommandFieldElement.U16, 2);
        }

        if (type == typeof(int))
        {
            return (CommandFieldElement.I32, 4);
        }

        if (type == typeof(uint))
        {
            return (CommandFieldElement.U32, 4);
        }

        if (type == typeof(long))
        {
            return (CommandFieldElement.I64, 8);
        }

        if (type == typeof(ulong))
        {
            return (CommandFieldElement.U64, 8);
        }

        if (type == typeof(float))
        {
            return (CommandFieldElement.F32, 4);
        }

        if (type == typeof(double))
        {
            return (CommandFieldElement.F64, 8);
        }

        // A multi-component field is a struct of contiguous floats or doubles — Vec2d, Vec3d, a Point4F, or the application's own pair. Its element width is
        // its size over the codec's component count: 16 bytes are four floats under a count of 4 and two doubles under a vec2, which a width guessed from the
        // size alone got wrong for every four-float shape. A size neither divides is refused by the caller, with both numbers named.
        if (type.IsValueType && !type.IsPrimitive)
        {
            var size = Marshal.SizeOf(type);
            if (size == components * 8 || (components <= 1 && size % 8 == 0))
            {
                return (CommandFieldElement.F64, 8);
            }

            if (size == components * 4 || (components <= 1 && size % 4 == 0))
            {
                return (CommandFieldElement.F32, 4);
            }
        }

        throw new InvalidOperationException(
            $"Command '{command}' field '{wireField}' reads '{fieldType.Name}', which is not a number this decoder can store. A command struct holds " +
            "integers, floats and enums, or a struct of contiguous floats or doubles for a vector codec. `bool` and `char` are excluded deliberately: " +
            "their marshalled width differs from their managed width, so the offsets would be computed against a layout that is not the one in memory.");
    }

    private static void RefuseUnsuitableStruct(Type structType, string name)
    {
        if (structType.StructLayoutAttribute is { Value: LayoutKind.Auto })
        {
            throw new InvalidOperationException(
                $"Command '{name}' uses '{structType.Name}', which declares LayoutKind.Auto. The decoder writes values at measured offsets, and an " +
                "auto-laid-out struct has none it may rely on — declare it sequential (the C# default) or explicit.");
        }

        foreach (var field in structType.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (field.FieldType == typeof(bool) || field.FieldType == typeof(char))
            {
                throw new InvalidOperationException(
                    $"Command '{name}' field '{structType.Name}.{field.Name}' is a {field.FieldType.Name}, whose marshalled width differs from its managed " +
                    "width; every offset in the struct after it would be computed against the wrong layout. Use a byte for a flag.");
            }
        }
    }

    private static string[] UnboundFields(Type structType, HashSet<string> bound)
    {
        List<string> unbound = null;
        foreach (var field in structType.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!bound.Contains(field.Name))
            {
                (unbound ??= []).Add(field.Name);
            }
        }

        return unbound == null ? [] : unbound.ToArray();
    }
}

/// <summary>
/// The network identity of every replicated entity, so a command naming a <c>netId</c> can be resolved back to the entity it means.
/// </summary>
/// <remarks>
/// <para>
/// <b>It exists here because the wire has no other way back.</b> A command's entity reference is a <c>u32</c> netId (01-model § 7); the allocator knows an
/// identity's generation but not what holds it, and the per-entity replication blocks map the other way — entity to identity. Without this table
/// <c>TryResolve</c> could never answer, so the surface would be a method that always says no.
/// </para>
/// <para>
/// <b>Written by the tick, read by the tick.</b> Identities are bound where they are assigned (the projection pass) and unbound where they are released, both
/// inside the replication track; a transport thread never touches it. The volatile loads on the array are for the drain reading what an earlier stage of the
/// same tick wrote on another worker.
/// </para>
/// </remarks>
internal sealed class NetIdEntityIndex
{
    private EntityId[] _entities = [];
    private readonly Lock _growth = new();

    /// <summary>
    /// Grows the table to hold every identity up to <paramref name="highWaterMark"/>. Serial, before the projection's chunks: a <see cref="Bind"/> from a chunk
    /// never grows it, so two workers binding different identities never race a growth that would drop one of their writes.
    /// </summary>
    /// <param name="highWaterMark">The highest identity the allocator has handed out, leases included.</param>
    public void Reserve(uint highWaterMark) => EnsureCapacity(highWaterMark);

    /// <summary>Binds an identity to the entity holding it. From the projection's chunks: each identity is one worker's, and the table was reserved.</summary>
    /// <param name="netId">The identity.</param>
    /// <param name="entity">The entity.</param>
    public void Bind(uint netId, EntityId entity)
    {
        if (netId == NetIdAllocator.NoNetId)
        {
            return;
        }

        if (netId >= (uint)_entities.Length)
        {
            // Not reserved: a caller outside the projection (a test), alone. A projection chunk never gets here — Reserve ran before it — and growing under
            // one would lose another chunk's write.
            Debug.Assert(false, "NetIdEntityIndex.Bind past the reserved capacity");
            EnsureCapacity(netId);
        }

        _entities[netId] = entity;
    }

    /// <summary>Forgets an identity, so a stale reference to it resolves to nothing rather than to whoever holds it next.</summary>
    /// <param name="netId">The identity.</param>
    public void Unbind(uint netId)
    {
        if (netId == NetIdAllocator.NoNetId || netId >= (uint)_entities.Length)
        {
            return;
        }

        _entities[netId] = EntityId.Null;
    }

    /// <summary>The entity an identity names.</summary>
    /// <param name="netId">The identity.</param>
    /// <param name="entity">The entity, or <see cref="EntityId.Null"/>.</param>
    /// <returns><see langword="false"/> when nothing holds that identity.</returns>
    public bool TryGet(uint netId, out EntityId entity)
    {
        var table = Volatile.Read(ref _entities);
        if (netId == NetIdAllocator.NoNetId || netId >= (uint)table.Length)
        {
            entity = EntityId.Null;
            return false;
        }

        entity = table[netId];
        return !entity.IsNull;
    }

    /// <summary>Drops every binding.</summary>
    public void Clear()
    {
        lock (_growth)
        {
            Array.Clear(_entities);
        }
    }

    private void EnsureCapacity(uint netId)
    {
        if (netId < (uint)_entities.Length)
        {
            return;
        }

        lock (_growth)
        {
            if (netId < (uint)_entities.Length)
            {
                return;
            }

            var capacity = Math.Max(256, _entities.Length);
            while (netId >= (uint)capacity)
            {
                capacity *= 2;
            }

            var grown = new EntityId[capacity];
            Array.Copy(_entities, grown, _entities.Length);

            // Published as a whole: a reader walking the old array keeps a consistent view, and one that picks up the new array sees every binding copied into
            // it, because the copy happens before the reference is stored.
            Volatile.Write(ref _entities, grown);
        }
    }
}
