using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace Typhon.Engine;

/// <summary>
/// Declares a group set (<c>claude/design/Subscriptions/14-phase4-design.md</c> § 3): a family of groups, each a key that members carry in a field of one
/// of their components, and the selector that decides which keys a session holds.
/// </summary>
/// <remarks>
/// <para>
/// <b>Membership is the engine's own index, never a copy.</b> A member field carries <c>[Index(AllowMultiple = true, Sparse = true)]</c>: 0 means "in no
/// group" and is not indexed, every commit and fence path maintains it, and a crash rebuilds it from the data. The set reads that index to fill a key a
/// session comes to hold, and the projection reads the field on every push, so a change of key is an event like a change of position.
/// </para>
/// <para>
/// <b>A region set</b> (<see cref="Region{TRegion}"/>) holds the keys whose extent the session's observer meets: a region is an entity of the declared
/// archetype carrying the key and the extent, and a profile names the set with <see cref="ObserverBuilder.Groups(string, double)"/> and the reach it is
/// met at.
/// </para>
/// <para>
/// <b>An explicit set</b> (<see cref="Explicit"/>) holds the keys a session asked for and the application allowed — a client's <c>SubscribeRequest</c> —
/// and the keys the server chose for it (<see cref="SessionRequest.SetSources"/>). <b>A shared set</b> (<see cref="Shared"/>) has one key, held by every
/// session whose profile names it. A profile names either with <see cref="ObserverBuilder.Groups(string)"/>, which takes no reach.
/// </para>
/// </remarks>
[PublicAPI]
public sealed class GroupSetBuilder
{
    private readonly GroupSetDeclaration _set;

    internal GroupSetBuilder(GroupSetDeclaration set) => _set = set;

    /// <summary>
    /// Selects this set's keys by geometry: the regions of <typeparamref name="TRegion"/> whose extent a session's observer meets.
    /// </summary>
    /// <typeparam name="TRegion">The region archetype: one entity per group, carrying the key and the extent.</typeparam>
    /// <param name="configure">Names the region's key and extent fields.</param>
    /// <returns>This builder.</returns>
    public GroupSetBuilder Region<TRegion>(Action<RegionBuilder> configure) where TRegion : Archetype<TRegion>
    {
        ArgumentNullException.ThrowIfNull(configure);
        if (_set.Region != null)
        {
            throw new InvalidOperationException($"Group set '{_set.Name}' already declares its region archetype.");
        }

        ThrowIfSelected();
        var region = new RegionDeclaration(typeof(TRegion));
        configure(new RegionBuilder(region));
        if (region.KeyField == null || region.ExtentField == null)
        {
            throw new InvalidOperationException($"Group set '{_set.Name}': a region names both its key (Key) and its extent (Extent).");
        }

        _set.Region = region;
        _set.Selector = GroupSelector.Region;
        return this;
    }

    /// <summary>
    /// Selects this set's keys by request (14 § 4, P13–P14): a session holds a key its client asked for with <c>SubscribeRequest</c> once
    /// <paramref name="admit"/> allowed it, and the keys a system chose for it with <see cref="SessionRequest.SetSources"/>.
    /// </summary>
    /// <param name="admit">
    /// The application's verdict on each client request: synchronous, given the session, the set and the key, and nothing of the engine's. It runs on the
    /// tick's ingress drain, which is chunked over sessions, so it may be called from several threads at once — like an admission check. Null: clients may
    /// not ask, and the set's keys are the server's alone.
    /// </param>
    /// <returns>This builder.</returns>
    public GroupSetBuilder Explicit(GroupRequestHandler admit = null)
    {
        ThrowIfSelected();
        _set.Admit = admit;
        _set.Selector = GroupSelector.Explicit;
        return this;
    }

    /// <summary>
    /// Makes this a set with one group (14 § 4, P15): every session whose profile names the set holds <paramref name="key"/> from its first frame, filled
    /// and completed like a region's.
    /// </summary>
    /// <param name="key">The group's key: what the members' field holds, never 0, which is no group.</param>
    /// <returns>This builder.</returns>
    public GroupSetBuilder Shared(long key)
    {
        ThrowIfSelected();
        if (key == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(key), key, "A shared set's key is what its members hold; 0 means no group and is never indexed.");
        }

        _set.SharedKey = key;
        _set.Selector = GroupSelector.Shared;
        return this;
    }

    private void ThrowIfSelected()
    {
        if (_set.Selector != GroupSelector.None)
        {
            throw new InvalidOperationException($"Group set '{_set.Name}' already selects its keys by {_set.Selector}: a set has one selector.");
        }
    }

    /// <summary>Adds a member archetype: its entities belong to the group whose key their field holds, none when it holds 0.</summary>
    /// <typeparam name="TArchetype">The member archetype.</typeparam>
    /// <param name="configure">Names the key field.</param>
    /// <returns>This builder.</returns>
    public GroupSetBuilder Member<TArchetype>(Action<MemberBuilder> configure) where TArchetype : Archetype<TArchetype>
    {
        ArgumentNullException.ThrowIfNull(configure);
        foreach (var existing in _set.Members)
        {
            if (existing.Archetype == typeof(TArchetype))
            {
                throw new InvalidOperationException($"Group set '{_set.Name}' already names '{typeof(TArchetype).Name}' as a member.");
            }
        }

        var member = new MemberDeclaration(typeof(TArchetype));
        configure(new MemberBuilder(member));
        if (member.KeyField == null)
        {
            throw new InvalidOperationException($"Group set '{_set.Name}': member '{typeof(TArchetype).Name}' names no key field (Key).");
        }

        _set.AddMember(member);
        return this;
    }

    /// <summary>The hysteresis on the extent test: a region held is left only beyond its reach plus this margin.</summary>
    /// <param name="marginM">Metres, at least 0. Defaults to 0.</param>
    /// <returns>This builder.</returns>
    public GroupSetBuilder Leave(double marginM)
    {
        if (!double.IsFinite(marginM) || marginM < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(marginM), marginM, "A leave margin is a finite distance of at least 0.");
        }

        _set.LeaveMarginM = marginM;
        return this;
    }
}

/// <summary>Names a region archetype's key and extent fields (<see cref="GroupSetBuilder.Region{TRegion}"/>).</summary>
[PublicAPI]
public sealed class RegionBuilder
{
    private readonly RegionDeclaration _region;

    internal RegionBuilder(RegionDeclaration region) => _region = region;

    /// <summary>The field holding the region's key: an integer field, never 0 for a region that exists.</summary>
    /// <typeparam name="TComponent">The component holding the field.</typeparam>
    /// <typeparam name="TKey">The key's integer type.</typeparam>
    /// <param name="component">The component handle.</param>
    /// <param name="selector">Selects the field, as one member access.</param>
    /// <returns>This builder.</returns>
    public RegionBuilder Key<TComponent, TKey>(Comp<TComponent> component, Expression<Func<TComponent, TKey>> selector) where TComponent : unmanaged
    {
        _region.KeyComponent = SubscriptionsNames.ComponentTypeId(component);
        _region.KeyField = SubscriptionsNames.SelectorField(selector, "Key");
        return this;
    }

    /// <summary>The field holding the region's extent: an <c>AABB2F</c>, <c>AABB3F</c>, <c>BSphere2F</c> or <c>BSphere3F</c>.</summary>
    /// <typeparam name="TComponent">The component holding the field.</typeparam>
    /// <typeparam name="TExtent">The extent's type.</typeparam>
    /// <param name="component">The component handle.</param>
    /// <param name="selector">Selects the field, as one member access.</param>
    /// <returns>This builder.</returns>
    public RegionBuilder Extent<TComponent, TExtent>(Comp<TComponent> component, Expression<Func<TComponent, TExtent>> selector)
        where TComponent : unmanaged where TExtent : unmanaged
    {
        _region.ExtentComponent = SubscriptionsNames.ComponentTypeId(component);
        _region.ExtentField = SubscriptionsNames.SelectorField(selector, "Extent");
        return this;
    }
}

/// <summary>Names a member archetype's key field (<see cref="GroupSetBuilder.Member{TArchetype}"/>).</summary>
[PublicAPI]
public sealed class MemberBuilder
{
    private readonly MemberDeclaration _member;

    internal MemberBuilder(MemberDeclaration member) => _member = member;

    /// <summary>The field holding the member's key: an integer field carrying <c>[Index(AllowMultiple = true, Sparse = true)]</c>, 0 for no group.</summary>
    /// <typeparam name="TComponent">The component holding the field.</typeparam>
    /// <typeparam name="TKey">The key's integer type.</typeparam>
    /// <param name="component">The component handle.</param>
    /// <param name="selector">Selects the field, as one member access.</param>
    /// <returns>This builder.</returns>
    public MemberBuilder Key<TComponent, TKey>(Comp<TComponent> component, Expression<Func<TComponent, TKey>> selector) where TComponent : unmanaged
    {
        _member.KeyComponent = SubscriptionsNames.ComponentTypeId(component);
        _member.KeyField = SubscriptionsNames.SelectorField(selector, "Key");
        return this;
    }
}

/// <summary>A declared group set, as <see cref="SubscriptionsRegistry.Groups"/> recorded it.</summary>
[PublicAPI]
public sealed class GroupSetDeclaration
{
    internal GroupSetDeclaration(string name) => Name = name;

    /// <summary>The set's name, as profiles and the catalog name it.</summary>
    public string Name { get; }

    /// <summary>The region selector; <see langword="null"/> unless <see cref="GroupSetBuilder.Region{TRegion}"/> declared one.</summary>
    public RegionDeclaration Region { get; internal set; }

    /// <summary>What decides which keys a session holds; <see cref="GroupSelector.None"/> until one is declared, which <c>Start</c> refuses.</summary>
    public GroupSelector Selector { get; internal set; }

    /// <summary>An explicit set's verdict on a client's request; <see langword="null"/> when clients may not ask.</summary>
    public GroupRequestHandler Admit { get; internal set; }

    /// <summary>A shared set's one key; 0 for any other set.</summary>
    public long SharedKey { get; internal set; }

    private readonly List<MemberDeclaration> _members = [];

    /// <summary>The member archetypes.</summary>
    public IReadOnlyList<MemberDeclaration> Members => _members;

    internal void AddMember(MemberDeclaration member) => _members.Add(member);

    /// <summary>The hysteresis on the extent test, metres.</summary>
    public double LeaveMarginM { get; internal set; }

    /// <inheritdoc />
    public override string ToString() => $"{Name}: {Members.Count} member archetype(s)";
}

/// <summary>A group set's region archetype and its key and extent fields.</summary>
[PublicAPI]
public sealed class RegionDeclaration
{
    internal RegionDeclaration(Type archetype) => Archetype = archetype;

    /// <summary>The region archetype.</summary>
    public Type Archetype { get; }

    internal int KeyComponent;
    internal int ExtentComponent;

    /// <summary>The key field's name.</summary>
    public string KeyField { get; internal set; }

    /// <summary>The extent field's name.</summary>
    public string ExtentField { get; internal set; }
}

/// <summary>A group set's member archetype and its key field.</summary>
[PublicAPI]
public sealed class MemberDeclaration
{
    internal MemberDeclaration(Type archetype) => Archetype = archetype;

    /// <summary>The member archetype.</summary>
    public Type Archetype { get; }

    internal int KeyComponent;

    /// <summary>The key field's name.</summary>
    public string KeyField { get; internal set; }
}

/// <summary>A profile observer's interest in a group set: the set, and for a region set the reach its extents are met at.</summary>
/// <param name="Set">The group set's name.</param>
/// <param name="ReachM">
/// The reach, metres: a region is met when the observer's anchor is within it of the region's extent. <see cref="double.NaN"/> for an explicit or a
/// shared set, which <see cref="ObserverBuilder.Groups(string)"/> names with none.
/// </param>
[PublicAPI]
public readonly record struct GroupInterest(string Set, double ReachM)
{
    /// <summary>Whether the interest names a reach: a region set's interest does, an explicit or a shared one's does not.</summary>
    public bool HasReach => !double.IsNaN(ReachM);
}

/// <summary>What decides which keys of a group set a session holds (14 § 3.1, P1).</summary>
[PublicAPI]
public enum GroupSelector
{
    /// <summary>Not declared yet; refused at <c>Start</c>.</summary>
    None = 0,

    /// <summary>The keys whose region extent the session's observer meets.</summary>
    Region = 1,

    /// <summary>The keys the session's client asked for and the application allowed, and the ones a system chose for it.</summary>
    Explicit = 2,

    /// <summary>The set's one key, for every session whose profile names it.</summary>
    Shared = 3,
}

/// <summary>A client's request for one key of an explicit group set, as the application's verdict sees it (14 § 4, P13).</summary>
[PublicAPI]
public readonly struct GroupRequest
{
    internal GroupRequest(SessionId session, string set, long key)
    {
        Session = session;
        Set = set;
        Key = key;
    }

    /// <summary>The session asking.</summary>
    public SessionId Session { get; }

    /// <summary>The group set's name.</summary>
    public string Set { get; }

    /// <summary>The key asked for: never 0, which the engine refuses before asking.</summary>
    public long Key { get; }
}

/// <summary>The application's verdict on a <see cref="GroupRequest"/>.</summary>
[PublicAPI]
public readonly struct GroupDecision
{
    private GroupDecision(bool allowed, ushort code)
    {
        Allowed = allowed;
        Code = code;
    }

    /// <summary>Whether the session may hold the key.</summary>
    public bool Allowed { get; }

    /// <summary>The <c>SOURCE_ERROR</c> code a refusal answers with; 0 when allowed.</summary>
    public ushort Code { get; }

    /// <summary>The session holds the key: it is filled, and the request answered <c>SOURCE_APPLIED</c> once it is complete.</summary>
    public static GroupDecision Allow => new(true, 0);

    /// <summary>The request is refused with <see cref="Typhon.Protocol.SourceErrorCodes.Denied"/>.</summary>
    public static GroupDecision Deny() => new(false, Typhon.Protocol.SourceErrorCodes.Denied);

    /// <summary>The request is refused with the application's own code.</summary>
    /// <param name="code">At least <see cref="Typhon.Protocol.SourceErrorCodes.FirstApplicationCode"/>: the codes below are the engine's.</param>
    /// <returns>The verdict.</returns>
    public static GroupDecision Deny(ushort code)
    {
        if (code < Typhon.Protocol.SourceErrorCodes.FirstApplicationCode)
        {
            throw new ArgumentOutOfRangeException(nameof(code), code,
                $"An application's SOURCE_ERROR code is at least {Typhon.Protocol.SourceErrorCodes.FirstApplicationCode}: the ones below are the engine's.");
        }

        return new GroupDecision(false, code);
    }
}

/// <summary>Decides a client's request for a key of an explicit group set (<see cref="GroupSetBuilder.Explicit"/>).</summary>
/// <param name="request">The session, the set and the key.</param>
/// <returns><see cref="GroupDecision.Allow"/> or a <see cref="GroupDecision.Deny()"/>.</returns>
public delegate GroupDecision GroupRequestHandler(in GroupRequest request);
