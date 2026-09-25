using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using Typhon.Protocol;

namespace Typhon.Engine;

/// <summary>
/// How a command type's arrivals are delivered into a tick.
/// </summary>
[PublicAPI]
public enum CommandCoalesce
{
    /// <summary>Every command is delivered, in the order that session sent them.</summary>
    Queued = 0,

    /// <summary>
    /// Only the newest per session per tick survives. For a continuous intent — where to walk, where to look — an older one is already wrong.
    /// </summary>
    LatestPerSession = 1,
}

/// <summary>
/// A stateless, syntactic check run on the transport thread before a command ever reaches the tick.
/// </summary>
/// <typeparam name="T">The command type.</typeparam>
/// <param name="command">The decoded command.</param>
/// <returns><see langword="true"/> to let the command through.</returns>
/// <remarks>
/// It sees no engine data, deliberately: semantic validation — cooldowns, ownership, range against the current world — belongs in the system that applies
/// the command, where the state it must agree with actually is. This rejects the impossible, not the disallowed.
/// </remarks>
[PublicAPI]
public delegate bool CommandPrecheck<T>(in T command) where T : unmanaged;

/// <summary>
/// Declares a command type: what it is called on the wire, how fast a session may send it, who may send it, and what it must look like to be worth decoding.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every public instance field of <typeparamref name="T"/> travels, whether or not the declaration mentions it</b> (01-model § 7: "fields default to their
/// raw type"). <see cref="Field{TField}"/> OVERRIDES how one of them travels; <see cref="Ignore{TField}"/> keeps one off the wire. Silence declares nothing,
/// deliberately: a command whose author forgot a field would otherwise arrive zeroed on a server that had no way to say so.
/// </para>
/// <para>
/// <b>The struct's layout does not define the wire.</b> Fields travel in the catalog's canonical order and are decoded one at a time, so reordering members
/// of the C# struct is not a silent protocol break — which is exactly what canonicalization exists to prevent.
/// </para>
/// <para>
/// <b>The rate limit is enforced on the transport thread</b>, before the ring, so a client flooding a command type costs the tick nothing.
/// </para>
/// </remarks>
/// <typeparam name="T">The command type: an unmanaged struct, declared in an assembly a client can reference without the engine.</typeparam>
[PublicAPI]
public sealed class CommandBuilder<T> where T : unmanaged
{
    private readonly CommandDeclaration _command;

    internal CommandBuilder(CommandDeclaration command) => _command = command;

    /// <summary>Overrides the wire name, which is the type's own name by default.</summary>
    /// <param name="name">The wire name.</param>
    /// <returns>This builder.</returns>
    public CommandBuilder<T> Name(string name)
    {
        _command.Rename(name);
        return this;
    }

    /// <summary>Sets how arrivals are delivered into a tick.</summary>
    /// <param name="mode">Queued, or newest-per-session.</param>
    /// <returns>This builder.</returns>
    public CommandBuilder<T> Coalesce(CommandCoalesce mode)
    {
        _command.Coalesce = mode;
        return this;
    }

    /// <summary>
    /// The per-session token bucket for this command type.
    /// </summary>
    /// <param name="perSecond">Sustained rate, above zero.</param>
    /// <param name="burst">Bucket depth — how many may arrive at once after a quiet period. At least <paramref name="perSecond"/>.</param>
    /// <returns>This builder.</returns>
    public CommandBuilder<T> Rate(int perSecond, int burst)
    {
        if (perSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(perSecond), perSecond, "A command rate is a positive number per second.");
        }

        if (burst < perSecond)
        {
            throw new ArgumentOutOfRangeException(nameof(burst), burst, "A burst below the sustained rate would throttle a client that is within its rate.");
        }

        _command.RatePerSecond = perSecond;
        _command.RateBurst = burst;
        return this;
    }

    /// <summary>
    /// The roles allowed to send this command. Declaring none accepts every role.
    /// </summary>
    /// <param name="roles">The accepted roles.</param>
    /// <returns>This builder.</returns>
    public CommandBuilder<T> Roles(params SessionRole[] roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        _command.SetRoles(roles);
        return this;
    }

    /// <summary>Attaches the syntactic pre-check run before the command enters the tick.</summary>
    /// <param name="precheck">The check.</param>
    /// <returns>This builder.</returns>
    /// <remarks>
    /// The adapter is captured here, where <typeparamref name="T"/> is a compile-time argument, because the ingress path that calls the check holds only a
    /// <see cref="Type"/> and raw payload bytes. Reaching a typed delegate from a <see cref="Type"/> would take <c>MakeGenericType</c> over a value type,
    /// which is the AOT blocker class #409 names; a <c>static</c> lambda here is one cached instance per command type and no reflection at all.
    /// </remarks>
    public CommandBuilder<T> Precheck(CommandPrecheck<T> precheck)
    {
        ArgumentNullException.ThrowIfNull(precheck);
        _command.Precheck = precheck;
        _command.PrecheckAdapter = static (check, payload) => ((CommandPrecheck<T>)check)(in MemoryMarshal.AsRef<T>(payload));
        return this;
    }

    /// <summary>
    /// Overrides how one of the command's fields travels — a position quantized over the grid, a reference sent as a netId — instead of its raw type.
    /// </summary>
    /// <typeparam name="TField">The field's type.</typeparam>
    /// <param name="selector">Selects the field.</param>
    /// <param name="codec">How the value travels.</param>
    /// <param name="name">The wire name. <see langword="null"/> takes the field's own name.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">
    /// The selector names something that is not a public instance field of <typeparamref name="T"/>, or that field is already declared or ignored.
    /// </exception>
    /// <remarks>
    /// It is an override, not the thing that makes the field travel: a field nobody mentions travels under the codec its CLR type defaults to. What this buys
    /// is the cases a type cannot imply — a pair of floats that is a world position, a <c>u32</c> that is a netId, a range that deserves 8 bits instead of 32.
    /// </remarks>
    public CommandBuilder<T> Field<TField>(Expression<Func<T, TField>> selector, Codec codec, string name = null)
    {
        var field = SubscriptionsNames.BuildMessageField<T, TField>(selector, codec, name, typeof(T).Name);
        MessageContract.RequireDeclarableField(typeof(T), "Command", _command.Name, field.SourceFieldName, nameof(Field));
        _command.AddField(field);
        return this;
    }

    /// <summary>
    /// Keeps one of the command's fields off the wire: scratch space, a field the server fills in, a value the client has no business sending.
    /// </summary>
    /// <typeparam name="TField">The field's type.</typeparam>
    /// <param name="selector">Selects the field.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">
    /// The selector names something that is not a public instance field of <typeparamref name="T"/>, or that field already carries a
    /// <see cref="Field{TField}"/> declaration.
    /// </exception>
    /// <remarks>
    /// <b>It takes a verb because silence must not mean "not replicated".</b> Every other field travels by default, so leaving one out of the declaration is
    /// indistinguishable from forgetting it — and the failure that produces is a field that arrives zeroed for ever, discovered by a player. Saying so costs
    /// one line and makes the omission reviewable.
    /// </remarks>
    public CommandBuilder<T> Ignore<TField>(Expression<Func<T, TField>> selector)
    {
        var source = SubscriptionsNames.SelectorField(selector, nameof(Ignore));
        MessageContract.RequireDeclarableField(typeof(T), "Command", _command.Name, source, nameof(Ignore));
        _command.IgnoreField(source);
        return this;
    }
}

/// <summary>
/// What a <see cref="CommandBuilder{T}"/> declared.
/// </summary>
[PublicAPI]
public sealed class CommandDeclaration
{
    private readonly List<ProjectedField> _overrides = [];
    private readonly HashSet<string> _ignored = new(StringComparer.Ordinal);
    private SessionRole[] _roles = [];

    // The complete field set, materialized the first time anything reads it — which is Start, where the catalog is built. It cannot be computed in the
    // builder's constructor, because Field() and Ignore() run after it and are what resolve a field the CLR type alone cannot answer for.
    private ProjectedField[] _fields;
    private Dictionary<string, Type> _enumTypes;

    internal CommandDeclaration(Type commandType, int index, MessageFieldDeclaration[] attributed = null)
    {
        CommandType = commandType;
        Name = commandType.Name;
        Index = index;
        Attributed = attributed;
    }

    /// <summary>The fields the command's attributes declare (<see cref="IReplicatedMessage"/>), or <see langword="null"/>.</summary>
    internal MessageFieldDeclaration[] Attributed { get; }

    /// <summary>The command's CLR type.</summary>
    public Type CommandType { get; }

    /// <summary>The name it travels under.</summary>
    public string Name { get; private set; }

    /// <summary>The order this command was declared in. Not the wire index, which is assigned when the catalog is built.</summary>
    public int Index { get; }

    /// <summary>How arrivals are delivered into a tick.</summary>
    public CommandCoalesce Coalesce { get; internal set; }

    /// <summary>The sustained per-session rate; 0 when the declaration left it to the engine.</summary>
    public int RatePerSecond { get; internal set; }

    /// <summary>The per-session burst depth; 0 when the declaration left it to the engine.</summary>
    public int RateBurst { get; internal set; }

    /// <summary>The roles allowed to send it. Empty means every role.</summary>
    public IReadOnlyList<SessionRole> AllowedRoles => _roles;

    /// <summary>
    /// Every field that travels: the ones the declaration gave a codec, in declaration order, then the rest of the struct's public instance fields under the
    /// codec their CLR type defaults to, ordinally by name.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A field's CLR type has no default codec — a <see cref="long"/>, a <see cref="ulong"/>, a <see cref="double"/> or a type the wire cannot imply — and the
    /// declaration neither gave it one nor ignored it.
    /// </exception>
    /// <remarks>
    /// <b>Order here is declaration order, not wire order.</b> Wire order is the layout key of 03-wire-protocol § 12 W11 and is assigned once, by
    /// <see cref="CatalogSerializer.Canonicalize"/>, for archetypes, events and commands alike — so this list is free to stay readable and nothing sorts twice.
    /// </remarks>
    public IReadOnlyList<ProjectedField> Fields => Complete();

    /// <summary>The struct fields the declaration deliberately kept off the wire, in no particular order.</summary>
    public IReadOnlyCollection<string> IgnoredFields => _ignored;

    /// <summary>Whether a syntactic pre-check was attached.</summary>
    public bool HasPrecheck => Precheck != null;

    /// <summary>The syntactic pre-check, kept as a delegate the transport thread calls.</summary>
    internal Delegate Precheck { get; set; }

    /// <summary>
    /// Calls <see cref="Precheck"/> against raw payload bytes, so the ingress path can run it without naming the command's struct type.
    /// </summary>
    /// <remarks>
    /// Captured by <see cref="CommandBuilder{T}.Precheck"/>, where the struct type is a compile-time generic argument. <see langword="null"/> when the
    /// declaration attached no check.
    /// </remarks>
    internal CommandPrecheckAdapter PrecheckAdapter { get; set; }

    /// <inheritdoc/>
    public override string ToString() => $"{Name} ({Coalesce})";

    internal void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A command needs a name.", nameof(name));
        }

        SubscriptionsNames.RefuseBuiltInCommandName(name);
        Name = name;
    }

    internal void SetRoles(SessionRole[] roles) => _roles = (SessionRole[])roles.Clone();

    /// <summary>The CLR enum a defaulted field's value set comes from, or <see langword="null"/>. An overridden field carries its own on the codec.</summary>
    internal IReadOnlyDictionary<string, Type> DefaultEnumTypes
    {
        get
        {
            Complete();
            return _enumTypes;
        }
    }

    internal void AddField(ProjectedField field)
    {
        MessageContract.RefuseFrozen(_fields, "Command", Name);
        foreach (var existing in _overrides)
        {
            if (string.Equals(existing.Name, field.Name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Command '{Name}' already declares a field named '{field.Name}'.");
            }
        }

        MessageContract.RefuseIgnoredOverride(_ignored, "Command", Name, field.SourceFieldName);
        _overrides.Add(field);
    }

    internal void IgnoreField(string sourceFieldName)
    {
        MessageContract.RefuseFrozen(_fields, "Command", Name);
        MessageContract.RefuseDeclaredIgnore(_overrides, "Command", Name, sourceFieldName);
        _ignored.Add(sourceFieldName);
    }

    /// <summary>Materializes the field set at the end of the declaring call, so a field with no default codec is refused where it was written.</summary>
    internal void CompleteDeclaration() => Complete();

    private ProjectedField[] Complete()
        => _fields ??= MessageContract.Complete(CommandType, "Command", Name, _overrides, _ignored, Attributed, out _enumTypes);
}

/// <summary>
/// What a message type — a command struct or an event record — puts on the wire when its declaration says nothing: every public instance field, under the
/// codec its CLR type implies.
/// </summary>
/// <remarks>
/// <para>
/// <b>Completeness is the point</b> (01-model § 7). A declaration that only OVERRIDES produces a wire the application never reviewed: the fields nobody
/// mentioned are absent from the catalog, so the struct behind them arrives zeroed and the only trace is a diagnostic array nobody reads
/// (<c>CommandRegistry.UnboundStructFields</c>). Defaulting turns "I forgot" into "I said so" — and <see cref="CommandBuilder{T}.Ignore{TField}"/> turns
/// "I meant to leave it out" into a line of code.
/// </para>
/// <para>
/// <b>The table refuses more than it accepts, and that is deliberate.</b> A 64-bit integer has no place on the wire at all (01-model § 2), and a
/// <see cref="double"/> has no default quantizer that is right for both a world coordinate and a ratio. Both are named at the declaration with the verb that
/// fixes them, because a wrong default here is a value that is silently wrong on every client rather than a build that fails once.
/// </para>
/// <para>
/// <b>It is reflection over a type, once, at <c>Start</c></b> — <see cref="Type.GetFields(BindingFlags)"/> and <see cref="Enum.GetNames(Type)"/>, which are
/// metadata the runtime already keeps. There is no <c>Reflection.Emit</c>, no <c>MakeGenericType</c> and no <c>Expression.Compile</c>, so the AOT constraint
/// of #409 holds.
/// </para>
/// </remarks>
internal static class MessageContract
{
    /// <summary>Refuses a selector that does not name a public instance field of the message type.</summary>
    /// <param name="messageType">The command struct or event record.</param>
    /// <param name="what">"Command" or "Event", for the message.</param>
    /// <param name="name">The declaration's name.</param>
    /// <param name="sourceFieldName">The field the selector resolved to.</param>
    /// <param name="verb">The builder verb that was called.</param>
    internal static void RequireDeclarableField(Type messageType, string what, string name, string sourceFieldName, string verb)
    {
        if (FieldsOf(messageType).ContainsKey(sourceFieldName))
        {
            return;
        }

        throw new InvalidOperationException(
            $"{what} '{name}' cannot {verb} '{messageType.Name}.{sourceFieldName}': it is not a public instance field of the struct, and only those travel. " +
            "A property, a static or a private member has no place in a layout the wire measures offsets against.");
    }

    /// <summary>Refuses a declaration arriving after the field set was materialized — it would change a wire a client already negotiated against.</summary>
    /// <param name="completed">The materialized set, or <see langword="null"/> while the declaration is still open.</param>
    /// <param name="what">"Command" or "Event".</param>
    /// <param name="name">The declaration's name.</param>
    internal static void RefuseFrozen(ProjectedField[] completed, string what, string name)
    {
        if (completed != null)
        {
            throw new InvalidOperationException(
                $"{what} '{name}' was already compiled into the catalog, so its fields are what clients negotiated against and cannot change.");
        }
    }

    /// <summary>Refuses giving a codec to a field the declaration already said it would not send.</summary>
    /// <param name="ignored">The ignored source field names.</param>
    /// <param name="what">"Command" or "Event".</param>
    /// <param name="name">The declaration's name.</param>
    /// <param name="sourceFieldName">The field being declared.</param>
    internal static void RefuseIgnoredOverride(HashSet<string> ignored, string what, string name, string sourceFieldName)
    {
        if (ignored.Contains(sourceFieldName))
        {
            throw new InvalidOperationException(
                $"{what} '{name}' ignores '{sourceFieldName}' and also declares a codec for it. One of the two is a mistake, and the engine cannot tell " +
                "which.");
        }
    }

    /// <summary>Refuses ignoring a field the declaration already gave a codec.</summary>
    /// <param name="declared">The declared overrides.</param>
    /// <param name="what">"Command" or "Event".</param>
    /// <param name="name">The declaration's name.</param>
    /// <param name="sourceFieldName">The field being ignored.</param>
    internal static void RefuseDeclaredIgnore(List<ProjectedField> declared, string what, string name, string sourceFieldName)
    {
        foreach (var field in declared)
        {
            if (string.Equals(field.SourceFieldName, sourceFieldName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"{what} '{name}' declares a codec for '{sourceFieldName}' and also ignores it. One of the two is a mistake, and the engine cannot tell " +
                    "which.");
            }
        }
    }

    /// <summary>
    /// The complete wire field set: the declared overrides in declaration order, then every remaining public instance field under its default codec, ordinally
    /// by name.
    /// </summary>
    /// <param name="messageType">The command struct or event record.</param>
    /// <param name="what">"Command" or "Event".</param>
    /// <param name="name">The declaration's name.</param>
    /// <param name="declared">The overrides, in declaration order.</param>
    /// <param name="ignored">The source fields kept off the wire.</param>
    /// <param name="attributed">
    /// The fields the type's attributes declare (design/Subscriptions/11 § 5), or <see langword="null"/>: between the builder's overrides, which win, and
    /// the type defaults, which they replace.
    /// </param>
    /// <param name="enumTypes">Filled with the CLR enum behind each defaulted enum field, keyed by wire name; empty when there is none.</param>
    /// <returns>The complete set.</returns>
    /// <exception cref="InvalidOperationException">A field has no default codec and the declaration neither gave it one nor ignored it.</exception>
    internal static ProjectedField[] Complete(Type messageType, string what, string name, List<ProjectedField> declared, HashSet<string> ignored,
        MessageFieldDeclaration[] attributed, out Dictionary<string, Type> enumTypes)
    {
        enumTypes = new Dictionary<string, Type>(StringComparer.Ordinal);

        var members = FieldsOf(messageType);
        var complete = new List<ProjectedField>(members.Count);
        var wireNames = new HashSet<string>(StringComparer.Ordinal);
        var bound = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in declared)
        {
            complete.Add(field);
            wireNames.Add(field.Name);
            bound.Add(field.SourceFieldName);
        }

        // Ordinal by name, so two builds of one declaration produce one list whatever order the runtime hands its metadata back in. The WIRE order is not this
        // one — CatalogSerializer.Canonicalize assigns it from W11's layout key — but a stable list here is what makes the catalog hash reproducible.
        var remaining = new List<string>(members.Count);
        foreach (var member in members)
        {
            if (!bound.Contains(member.Key) && !ignored.Contains(member.Key))
            {
                remaining.Add(member.Key);
            }
        }

        remaining.Sort(StringComparer.Ordinal);
        foreach (var source in remaining)
        {
            var member = members[source];
            var wire = source;
            Codec codec;
            Type enumType = null;
            if (TryAttributed(attributed, source, out var declaration))
            {
                // The attribute's codec, through the same factory a builder call uses: an invalid one throws here, naming the field.
                try
                {
                    codec = Codec.Declared(member.FieldType, declaration.Kind, declaration.Bits, declaration.Min, declaration.Max, declaration.Scale,
                        declaration.MaxBytes);
                    if (declaration.Saturate)
                    {
                        codec = codec.Saturate();
                    }
                }
                catch (Exception e) when (e is ArgumentException or NotSupportedException)
                {
                    throw new InvalidOperationException($"{what} '{name}' field '{messageType.Name}.{source}': its attribute declares {e.Message}", e);
                }

                // The same narrowing rule a builder Field call enforces: no 64-bit integer reaches the wire unless the declaration clamps it.
                if ((member.FieldType == typeof(long) || member.FieldType == typeof(ulong)) && !codec.Saturating)
                {
                    throw new InvalidOperationException(
                        $"{what} '{name}' field '{messageType.Name}.{source}' reads a 64-bit integer, and no 64-bit integer reaches the wire: set " +
                        "Saturate = true on its attribute, so the clamp is a decision rather than a truncation nobody sees.");
                }

                wire = string.IsNullOrEmpty(declaration.Name) ? source : declaration.Name;
            }
            else
            {
                codec = DefaultCodec(member.FieldType, out enumType);
                if (!codec.IsDeclared)
                {
                    throw Undeclarable(what, name, messageType, member);
                }
            }

            SubscriptionsNames.RefuseReservedName(wire, "A field");
            if (!wireNames.Add(wire))
            {
                throw new InvalidOperationException(
                    $"{what} '{name}' renames a field to '{wire}', which is already the name of another of '{messageType.Name}''s fields. Two fields cannot " +
                    "share a wire name; rename one, or ignore the field it collides with.");
            }

            if (enumType != null)
            {
                enumTypes[wire] = enumType;
            }

            complete.Add(new ProjectedField
            {
                Name = wire,
                ComponentName = messageType.Name,
                SourceFieldName = source,
                Codec = codec,
            });
        }

        return complete.ToArray();
    }

    private static bool TryAttributed(MessageFieldDeclaration[] attributed, string source, out MessageFieldDeclaration declaration)
    {
        foreach (var candidate in attributed ?? [])
        {
            if (string.Equals(candidate.Field, source, StringComparison.Ordinal))
            {
                declaration = candidate;
                return true;
            }
        }

        declaration = default;
        return false;
    }

    /// <summary>
    /// The codec a CLR type travels under when nothing said otherwise.
    /// </summary>
    /// <param name="fieldType">The struct field's type.</param>
    /// <param name="enumType">The CLR enum whose names the catalog must carry, or <see langword="null"/>.</param>
    /// <returns>The codec, or <c>default</c> when the type has no default and the declaration has to choose one.</returns>
    /// <remarks>
    /// <b>An enum's width is derived, and emitted explicitly</b> (W13): the narrowest <c>bits{n}</c> that indexes its whole name list. Derivation stays on the
    /// server and the wire stays a number plus a named value set, so a client that has never heard of the enum still decodes the integer.
    /// </remarks>
    internal static Codec DefaultCodec(Type fieldType, out Type enumType)
    {
        enumType = null;

        if (fieldType.IsEnum)
        {
            enumType = fieldType;
            return Codec.Bits(BitsFor(Enum.GetNames(fieldType).Length));
        }

        if (fieldType == typeof(bool))
        {
            return Codec.Bool;
        }

        if (fieldType == typeof(sbyte))
        {
            return Codec.I8;
        }

        if (fieldType == typeof(byte))
        {
            return Codec.U8;
        }

        if (fieldType == typeof(short))
        {
            return Codec.I16;
        }

        if (fieldType == typeof(ushort))
        {
            return Codec.U16;
        }

        if (fieldType == typeof(int))
        {
            return Codec.I32;
        }

        if (fieldType == typeof(uint))
        {
            return Codec.U32;
        }

        if (fieldType == typeof(float))
        {
            return Codec.F32;
        }

        if (fieldType == typeof(EntityId))
        {
            return Codec.EntityRef;
        }

        // long, ulong, double and everything else: no default. The caller names the field and the verb that fixes it.
        return default;
    }

    /// <summary>⌈log₂(count)⌉, at least one bit: the narrowest pack width that indexes every name.</summary>
    /// <param name="count">How many names the enum has.</param>
    /// <returns>The width in bits.</returns>
    private static int BitsFor(int count)
    {
        var bits = 1;
        var capacity = 2;
        while (capacity < count && bits < ProtocolConstants.MaxPackedBits)
        {
            capacity <<= 1;
            bits++;
        }

        return bits;
    }

    private static InvalidOperationException Undeclarable(string what, string name, Type messageType, FieldInfo member)
    {
        var where = $"{what} '{name}' field '{messageType.Name}.{member.Name}' is a {member.FieldType.Name}";
        if (member.FieldType == typeof(long) || member.FieldType == typeof(ulong))
        {
            return new InvalidOperationException(
                $"{where}, and no 64-bit integer reaches the wire (01-model § 2). Narrow it — " +
                $".Field(x => x.{member.Name}, Codec.VarUInt.Saturate()) clamps to 32 bits and counts the clamps — or keep it off the wire with " +
                $".Ignore(x => x.{member.Name}). It is not defaulted, because a silent truncation only shows up once the value is large.");
        }

        if (member.FieldType == typeof(double))
        {
            return new InvalidOperationException(
                $"{where}, and no default quantizes one: the step that is right for a world coordinate is wrong for a ratio and wrong again for an angle. " +
                $"Choose one — .Field(x => x.{member.Name}, Codec.Quant(min, max, bits)), Codec.Unorm(bits), Codec.Angle(bits), or Codec.F32 to send it at " +
                $"single precision — or keep it off the wire with .Ignore(x => x.{member.Name}).");
        }

        return new InvalidOperationException(
            $"{where}, which no codec travels by default. Declare how it travels with .Field(x => x.{member.Name}, <codec>) — a pair of coordinates is " +
            $"Codec.Pos2, a netId is Codec.EntityRef — or keep it off the wire with .Ignore(x => x.{member.Name}).");
    }

    /// <summary>The message type's public instance fields, by name.</summary>
    private static Dictionary<string, FieldInfo> FieldsOf(Type messageType)
    {
        var fields = messageType.GetFields(BindingFlags.Public | BindingFlags.Instance);
        var result = new Dictionary<string, FieldInfo>(fields.Length, StringComparer.Ordinal);
        foreach (var field in fields)
        {
            result[field.Name] = field;
        }

        return result;
    }
}
