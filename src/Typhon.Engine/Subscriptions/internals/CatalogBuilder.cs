using System;
using System.Collections.Generic;
using System.Globalization;
using Typhon.Protocol;

namespace Typhon.Engine.Internals;

/// <summary>
/// Emits the catalog a client decodes with, from the compiled projection plan the projection pass walks and the declarations the plan could not carry.
/// Runs once, at <c>Start</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The plan is the source, not the schema</b> (archive/Subscriptions/foundation/06 D1). Re-deriving the wire shape from <c>ComponentSchemaSpec</c>
/// would make "the catalog says what the encoder does" a thing to be reviewed; emitting it from the same structure S1 reads makes it true by construction.
/// Where the plan cannot carry something the wire needs — an <c>enum</c>'s value set, an event's routing, a command's rate — the declaration it was
/// compiled from is read beside it, by name, never a third source.
/// </para>
/// <para>
/// <b>Nothing about server memory leaves here</b> (archive/Subscriptions/foundation/06 D2, D3). A <see cref="CompiledField"/> carries a component slot, a
/// column offset, a stride and a field offset; not one of them is read by this file. What is read is the wire name, the codec, the section and the group
/// bit — which is the whole of what a byte stream means. No CLR type name is emitted either: an archetype, a field, a command and an event travel under
/// the name the projection declared, so renaming a C# type is not a wire break.
/// </para>
/// <para>
/// <b>Canonicalization is not repeated here.</b> Indices, sort order and the reserved ranges are <see cref="CatalogSerializer.Canonicalize"/>'s job and this
/// builder hands it declaration order on purpose: a second implementation of that ordering is a second thing to keep in step with the TypeScript decoder.
/// Every <c>Idx</c> written below is left at zero for exactly that reason.
/// </para>
/// <para>
/// <b>No reflection survives except the enum value set</b>, which is <see cref="Enum.GetNames(Type)"/> — metadata the runtime keeps for a type the declaration
/// already roots. There is no <c>Reflection.Emit</c>, no <c>DynamicMethod</c>, no <c>Expression.Compile</c> and no <c>Assembly.LoadFrom</c> on this path
/// (#409).
/// </para>
/// </remarks>
internal static class CatalogBuilder
{
    /// <summary>
    /// The application name emitted when nothing declared one.
    /// </summary>
    /// <remarks>
    /// A constant rather than the engine's or the entry assembly's name, and deliberately: the catalog's bytes have to be identical in two processes, and
    /// both of those differ between a test host and a server — <see cref="DatabaseEngine"/>'s default name carries a <see cref="Guid"/>. Until an
    /// <c>App(name, revision)</c> verb exists on the registration surface, every Typhon server's catalog names the engine.
    /// </remarks>
    internal const string DefaultAppName = "Typhon";

    /// <summary>
    /// Phase 1 has no resume: a dropped session reconnects. Emitted as zero rather than omitted, because the field is not optional and a client chooses
    /// between resuming and reconnecting from it.
    /// </summary>
    internal const int ResumeGraceMs = 0;

    /// <summary>
    /// Builds the catalog, canonicalizes it, serializes it and digests it — in one pass, so the bytes and the hash cannot describe two different declarations.
    /// </summary>
    /// <param name="registry">The frozen registry the plans were compiled from.</param>
    /// <param name="plans">The compiled plans, in declaration order.</param>
    /// <param name="appName">The application's name; <see langword="null"/> takes <see cref="DefaultAppName"/>.</param>
    /// <param name="appRevision">The application's own revision of its declarations.</param>
    /// <param name="tickPeriodUs">The nominal tick period in microseconds.</param>
    /// <param name="systemNames">
    /// The scheduled systems' names, in schedule order: the labels of the built-in <c>typhon.system.mean</c>. Empty omits that metric, which would otherwise
    /// be a vector metric carrying no values.
    /// </param>
    /// <returns>The canonical catalog, its UTF-8 bytes and their digest.</returns>
    /// <exception cref="CatalogException">The declarations produce a catalog that breaks a wire rule.</exception>
    public static CatalogExport Build(SubscriptionsRegistry registry, CompiledProjectionPlan[] plans, string appName, int appRevision, int tickPeriodUs,
        IReadOnlyList<string> systemNames)
    {
        ArgumentNullException.ThrowIfNull(registry);
        plans ??= [];

        // One dictionary, filled as fields are walked: an enum reaches the catalog because a field named it, never because a type exists.
        var enums = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var archetypes = new CatalogArchetype[plans.Length];
        for (var i = 0; i < plans.Length; i++)
        {
            archetypes[i] = BuildArchetype(registry, plans[i], enums);
        }

        var catalog = new Catalog
        {
            Protocol = new CatalogProtocolVersion { Major = ProtocolConstants.Major, Minor = ProtocolConstants.Minor },
            App = new CatalogApp { Name = string.IsNullOrWhiteSpace(appName) ? DefaultAppName : appName, Revision = appRevision },
            Tick = new CatalogTick { PeriodUs = tickPeriodUs, PingHz = registry.Options.PingHz },
            Limits = new CatalogLimits
            {
                FrameBytes = registry.Options.FrameBytes, ClientMessageBytes = registry.Options.ClientMessageBytes, ResumeGraceMs = ResumeGraceMs,
            },
            SessionKinds = Copy(registry.Sessions.DeclaredKinds),

            // The default kind "" and every declared one (12-realms § 1.4); the serializer puts them in canonical order, which a REALM's kindIdx indexes.
            RealmKinds = ["", .. registry.DeclaredRealmKinds],
            Archetypes = archetypes,
            Enums = enums,
            Events = BuildEvents(registry, enums),
            Commands = BuildCommands(registry, enums),

            // The aggregate tiers' grids (09 § 8): one per distinct tile edge and archetype set, over the spatial world. None without an aggregate.
            Grids = BuildGrids(registry, plans),
            Metrics = BuildMetrics(registry, archetypes, systemNames),
        };

        return CatalogSerializer.Export(catalog);
    }

    // ── Archetypes ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static CatalogArchetype BuildArchetype(SubscriptionsRegistry registry, CompiledProjectionPlan plan, Dictionary<string, string[]> enums)
    {
        var declared = DeclaredFieldsOf(registry, plan);
        var owner = plan.OwnerFields.Length == 0
            ? null
            : new CatalogOwner
            {
                Groups = GroupNames(plan.OwnerGroups),
                Fields = BuildFields(plan.Name, plan.OwnerFields, plan.OwnerGroups, declared, enums),
            };

        return new CatalogArchetype
        {
            Name = plan.Name,
            Groups = GroupNames(plan.Groups),
            Position = BuildPosition(plan.Position),
            Fields = BuildFields(plan.Name, plan.Fields, plan.Groups, declared, enums),
            Owner = owner,
        };
    }

    private static CatalogField[] BuildFields(string archetype, CompiledField[] fields, CompiledGroup[] groups, Dictionary<string, ProjectedField> declared,
        Dictionary<string, string[]> enums)
    {
        var result = new CatalogField[fields.Length];
        for (var i = 0; i < fields.Length; i++)
        {
            ref readonly var field = ref fields[i];

            // GroupBit is -1 for section 0, which is the onEnter section — and for a Static archetype, whose fields the compiler folds into it because a
            // record that is sent once and never updated belongs to no change group (W15).
            var onEnter = field.GroupBit < 0;
            if (!onEnter && (field.GroupBit >= groups.Length || groups[field.GroupBit].Name == null))
            {
                throw new InvalidOperationException(
                    $"Archetype '{archetype}' compiled field '{field.Name}' into group bit {field.GroupBit}, and the plan holds {groups.Length} group(s). " +
                    "The catalog names a field's group, so a bit with no group behind it would describe a mask bit no client could read.");
            }

            result[i] = new CatalogField
            {
                Name = field.Name,
                Codec = field.Codec,
                Group = onEnter ? null : groups[field.GroupBit].Name,
                OnEnter = onEnter,
                Enum = EnumOf(declared, field.Name, enums, $"archetype '{archetype}' field '{field.Name}'"),
            };
        }

        return result;
    }

    private static CatalogPosition BuildPosition(CompiledPosition position)
    {
        if (position == null)
        {
            return null;
        }

        // Tolerance, teleport speed and MaxAge are server policy no client reads (W16). They are not omitted for brevity: hashing them would re-send the
        // catalog to every connected client on every tuning change.
        return new CatalogPosition
        {
            Kind = position.Moving ? CatalogPosition.MotionKind : CatalogPosition.StaticKind,
            Model = !position.Moving ? null : position.Linear ? CatalogPosition.LinearModel : CatalogPosition.NoneModel,
            // Realm-framed (typhon.3): the width and bounds are the REALM block's, so the catalog names the kind alone.
            Pos = new CatalogCodec { Kind = position.Pos.Kind },
            Vel = position.Linear ? position.Vel : null,
        };
    }

    private static string[] GroupNames(CompiledGroup[] groups)
    {
        var names = new string[groups.Length];
        for (var i = 0; i < groups.Length; i++)
        {
            names[i] = groups[i].Name;
        }

        return names;
    }

    /// <summary>The archetype's declared fields by wire name — where an <c>enum</c>'s type lives, which the compiled plan does not carry.</summary>
    private static Dictionary<string, ProjectedField> DeclaredFieldsOf(SubscriptionsRegistry registry, CompiledProjectionPlan plan)
    {
        var result = new Dictionary<string, ProjectedField>(StringComparer.Ordinal);
        if (plan.DeclarationIndex < 0 || plan.DeclarationIndex >= registry.Archetypes.Count)
        {
            return result;
        }

        var projection = registry.Archetypes[plan.DeclarationIndex];
        foreach (var field in projection.Fields)
        {
            result[field.Name] = field;
        }

        foreach (var field in projection.OwnerFields)
        {
            result[field.Name] = field;
        }

        return result;
    }

    // ── Aggregate grids ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static CatalogGrid[] BuildGrids(SubscriptionsRegistry registry, CompiledProjectionPlan[] plans)
    {
        var grids = new List<CatalogGrid>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in registry.Profiles)
        {
            foreach (var observer in profile.Observers)
            {
                if (observer.Kind != ObserverKind.Aggregate)
                {
                    continue;
                }

                var archetypes = new List<int>();
                foreach (var type in observer.Archetypes)
                {
                    var index = Array.FindIndex(plans, p => p.ArchetypeType == type);
                    if (index >= 0 && !archetypes.Contains(index))
                    {
                        archetypes.Add(index);
                    }
                }

                archetypes.Sort();
                if (!seen.Add(observer.TileM.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "|" + string.Join(',', archetypes)))
                {
                    continue;
                }

                // The tile in replication cells (typhon.3, 12-realms § 5.4): the grid is laid over whichever realm the session is in.
                var cellM = registry.Options.ReplicationCellM;
                var cells = cellM > 0 ? observer.TileM / cellM : double.NaN;
                if (!double.IsFinite(cells) || Math.Abs(cells - Math.Round(cells)) > 1e-9 || Math.Round(cells) < 1)
                {
                    throw new NotSupportedException(
                        $"An Aggregate's tile of {observer.TileM} m is not a whole number of the {cellM} m replication cells: tile counts follow cell changes, "
                        + "so a tile edge inside a cell would let a move cross it unseen. Declare a multiple of SubscriptionsOptions.ReplicationCellM.");
                }

                grids.Add(new CatalogGrid { TileCells = (int)Math.Round(cells), Archetypes = archetypes.ToArray() });
            }
        }

        return grids.ToArray();
    }

    // ── Events and commands ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static CatalogEvent[] BuildEvents(SubscriptionsRegistry registry, Dictionary<string, string[]> enums)
    {
        // A catalog that declares events carries the built-in EventsLost too (09 § 11, D7): a skipped session is told what it was not sent.
        var declared = registry.Events.Count;
        var events = new CatalogEvent[declared == 0 ? 0 : declared + 1];
        if (declared > 0)
        {
            events[declared] = BuiltInEvents.CreateEventsLost();
        }

        for (var i = 0; i < declared; i++)
        {
            var declaration = registry.Events[i];
            events[i] = new CatalogEvent
            {
                Name = declaration.Name,
                Scope = ScopeOf(declaration),
                Fields = BuildMessageFields(declaration.Fields, declaration.DefaultEnumTypes, enums, $"event '{declaration.Name}'"),
            };
        }

        return events;
    }

    /// <summary>The routing token a client reads: how an event found it, which is the only part of routing that is not server-side.</summary>
    private static string ScopeOf(EventDeclaration declaration) => declaration.Routing switch
    {
        EventRouting.Near => "interest",
        EventRouting.ToKnown => "known",
        EventRouting.ToOwner => "owner",
        EventRouting.Broadcast => "broadcast",
        EventRouting.ToSession => BuiltInEvents.SessionScope,
        _ => throw new InvalidOperationException(
            $"Event '{declaration.Name}' declares no routing, so it would reach no session and the catalog would name a scope that means nothing. " +
            "Declare RouteNear, RouteToKnown, RouteToOwner, RouteToSession or Broadcast."),
    };

    private static CatalogCommand[] BuildCommands(SubscriptionsRegistry registry, Dictionary<string, string[]> enums)
    {
        var commands = new List<CatalogCommand>(registry.Commands.Count + 1);

        // W27: a built-in is listed only when it is enabled. ClientRegion is enabled by a profile declaring the observer that reads it, so a catalog that
        // named it unconditionally would tell every client it may send a footprint the server has nowhere to put.
        if (TryBuildClientRegion(registry, out var region))
        {
            commands.Add(region);
        }

        foreach (var declaration in registry.Commands)
        {
            commands.Add(new CatalogCommand
            {
                Name = declaration.Name,
                Delivery = declaration.Coalesce == CommandCoalesce.LatestPerSession ? CatalogCommand.LatestDelivery : CatalogCommand.QueuedDelivery,
                Rate = declaration.RatePerSecond <= 0
                    ? null
                    : new CatalogCommandRate { PerSec = declaration.RatePerSecond, Burst = declaration.RateBurst },
                Fields = BuildMessageFields(declaration.Fields, declaration.DefaultEnumTypes, enums, $"command '{declaration.Name}'"),
            });
        }

        return commands.ToArray();
    }

    private static bool TryBuildClientRegion(SubscriptionsRegistry registry, out CatalogCommand command)
    {
        command = null;
        ProfileDeclaration asking = null;
        foreach (var profile in registry.Profiles)
        {
            foreach (var observer in profile.Observers)
            {
                if (observer.Kind == ObserverKind.ClientRegion)
                {
                    asking = profile;
                    break;
                }
            }

            if (asking != null)
            {
                break;
            }
        }

        if (asking == null)
        {
            return false;
        }

        // Always list<pos3> over the session's realm frame (typhon.3, D-8): a flat realm ignores z.
        command = BuiltInCommands.CreateClientRegion();
        return true;
    }

    /// <summary>
    /// One catalog field per wire field of a message — an event's or a command's.
    /// </summary>
    /// <param name="fields">The declaration's complete field set: what it declared, plus what its CLR type defaulted to.</param>
    /// <param name="defaultEnums">The CLR enum behind each DEFAULTED enum field, by wire name. A declared field carries its own on the codec.</param>
    /// <param name="enums">The catalog's value sets, filled as fields are walked.</param>
    /// <param name="where">Names the message in a refusal.</param>
    /// <returns>The catalog fields, in the declaration's order; canonicalization puts them in wire order.</returns>
    /// <remarks>
    /// The two sources of an enum type are not redundancy. <see cref="Codec.Enum{TEnum}"/> carries the type because the application named it at a call site
    /// where it was a compile-time argument; a defaulted field's enum is only a <see cref="Type"/>, and reaching <c>Codec.Enum&lt;T&gt;</c> from one would take
    /// <c>MakeGenericMethod</c> over a value type — the AOT blocker class #409 names. So the declaration carries it beside the field instead.
    /// </remarks>
    private static CatalogField[] BuildMessageFields(IReadOnlyList<ProjectedField> fields, IReadOnlyDictionary<string, Type> defaultEnums,
        Dictionary<string, string[]> enums, string where)
    {
        var result = new CatalogField[fields.Count];
        for (var i = 0; i < result.Length; i++)
        {
            var field = fields[i];
            var enumType = field.Codec.EnumType;
            if (enumType == null && defaultEnums != null)
            {
                defaultEnums.TryGetValue(field.Name, out enumType);
            }

            result[i] = new CatalogField
            {
                Name = field.Name,
                Codec = field.Codec.Catalog,
                Enum = RegisterEnum(enums, enumType, $"{where} field '{field.Name}'"),
            };
        }

        return result;
    }

    // ── Metrics ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static CatalogMetric[] BuildMetrics(SubscriptionsRegistry registry, CatalogArchetype[] archetypes, IReadOnlyList<string> systemNames)
    {
        var metrics = new List<CatalogMetric>(BuiltInMetrics.Count + registry.Metrics.Count);
        var systemMean = BuiltInMetrics.ReservedIdx(BuiltInMetrics.SystemMean);
        var archetypeEntities = BuiltInMetrics.ReservedIdx(BuiltInMetrics.ArchetypeEntities);
        var systemLabels = Labels(systemNames);
        var archetypeLabels = ArchetypeLabels(archetypes);

        for (var idx = 0; idx < BuiltInMetrics.Count; idx++)
        {
            string[] labels = null;
            if (idx == systemMean || idx == archetypeEntities)
            {
                labels = idx == systemMean ? systemLabels : archetypeLabels;

                // A vector metric with no labels carries no values, and the wire has no way to say so. A runtime with no replicated archetype, or none of
                // whose systems has a name, simply does not publish that one.
                if (labels.Length == 0)
                {
                    continue;
                }
            }

            metrics.Add(BuiltInMetrics.Create(idx, labels));
        }

        foreach (var declaration in registry.Metrics)
        {
            metrics.Add(new CatalogMetric
            {
                Name = declaration.Name,
                Unit = declaration.Unit,
                Codec = declaration.Codec.Catalog,
                Kind = declaration.Kind == MetricKind.Counter ? CatalogMetric.CounterKind : null,
                Labels = declaration.Labels.Count == 0 ? null : Copy(declaration.Labels),
                Scope = declaration.PerSession ? CatalogMetric.SessionScope : null,
            });
        }

        return metrics.ToArray();
    }

    /// <summary>The archetype names in wire-index order, which is the ordinal sort canonicalization assigns indices from.</summary>
    private static string[] ArchetypeLabels(CatalogArchetype[] archetypes)
    {
        var names = new string[archetypes.Length];
        for (var i = 0; i < names.Length; i++)
        {
            names[i] = archetypes[i].Name;
        }

        Array.Sort(names, StringComparer.Ordinal);
        return names;
    }

    /// <summary>
    /// The labels of a positional vector metric: one per value, in order, and distinct.
    /// </summary>
    /// <remarks>
    /// Nothing makes two scheduled systems take different names, and the wire refuses a repeated label — a client keys its display by it. Dropping a
    /// duplicate would misalign every value after it, since the labels are positional, so a repeat is suffixed instead.
    /// </remarks>
    private static string[] Labels(IReadOnlyList<string> names)
    {
        if (names == null || names.Count == 0)
        {
            return [];
        }

        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<string>(names.Count);
        foreach (var name in names)
        {
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            if (!seen.TryGetValue(name, out var count))
            {
                seen[name] = 1;
                result.Add(name);
                continue;
            }

            seen[name] = count + 1;
            result.Add($"{name}#{(count + 1).ToString(CultureInfo.InvariantCulture)}");
        }

        return result.ToArray();
    }

    // ── Enums ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static string EnumOf(Dictionary<string, ProjectedField> declared, string name, Dictionary<string, string[]> enums, string where) =>
        declared.TryGetValue(name, out var field) ? RegisterEnum(enums, field.Codec.EnumType, where) : null;

    /// <summary>Records the enum's value set under its own name and returns that name, or <see langword="null"/> when there is none.</summary>
    private static string RegisterEnum(Dictionary<string, string[]> enums, Type enumType, string where)
    {
        if (enumType == null)
        {
            return null;
        }

        var key = enumType.Name;
        var names = DenseNames(enumType, where);
        if (!enums.TryGetValue(key, out var existing))
        {
            enums[key] = names;
            return key;
        }

        if (existing.Length != names.Length)
        {
            throw EnumCollision(key, where);
        }

        for (var i = 0; i < names.Length; i++)
        {
            if (!string.Equals(existing[i], names[i], StringComparison.Ordinal))
            {
                throw EnumCollision(key, where);
            }
        }

        return key;
    }

    private static InvalidOperationException EnumCollision(string key, string where) => new(
        $"Two different enums named '{key}' reach the catalog, one of them at {where}. The catalog keys a value set by its name, so a client would decode " +
        "one of the two fields against the other's names. Rename one of the enums.");

    /// <summary>
    /// <paramref name="enumType"/>'s names indexed by the integer that travels.
    /// </summary>
    /// <remarks>
    /// The catalog's contract is that a value's index in the array IS the integer on the wire (W13), so the values have to be 0, 1, 2 … with no gap, no
    /// negative and no alias. A gap cannot be expressed — the wire has no name for "nothing here" and an empty name is refused — so it is refused at
    /// <c>Start</c>, where the declaration can still be changed, rather than decoded as the wrong name by every client.
    /// </remarks>
    private static string[] DenseNames(Type enumType, string where)
    {
        var names = Enum.GetNames(enumType);
        var values = Enum.GetValuesAsUnderlyingType(enumType);
        for (var i = 0; i < names.Length; i++)
        {
            var value = Convert.ToInt64(values.GetValue(i), CultureInfo.InvariantCulture);
            if (value != i)
            {
                throw new InvalidOperationException(
                    $"Enum '{enumType.Name}' at {where} has '{names[i]}' = {value.ToString(CultureInfo.InvariantCulture)} where the catalog expects " +
                    $"{i.ToString(CultureInfo.InvariantCulture)}. A value's index in the catalog's name list is the integer that travels (W13), so the " +
                    "values have to run 0, 1, 2 … with no gap, no negative value and no alias.");
            }
        }

        return names;
    }

    private static string[] Copy(IReadOnlyList<string> values)
    {
        var result = new string[values.Count];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = values[i];
        }

        return result;
    }
}
