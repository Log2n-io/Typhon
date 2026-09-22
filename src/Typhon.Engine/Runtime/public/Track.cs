using JetBrains.Annotations;
using System;
using System.Collections.Generic;

namespace Typhon.Engine;

/// <summary>
/// An ordered, tagged container of <see cref="Dag"/>s — the top level of the runtime partitioning hierarchy (<c>Engine → Track → DAG → Phase → System</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Track order is the execution sequence:</b> every DAG of track <c>N</c> completes before any DAG of track <c>N+1</c> begins (a coarse engine-level barrier).
/// Within a track, DAGs are independent.
/// </para>
/// <para>
/// A track carries an open tag set (e.g. <see cref="EngineTag"/>). No engine behaviour keys off a track's <see cref="Name"/>; tooling visibility keys
/// off <see cref="Tags"/>. The three built-in tracks — Engine-Pre, <see cref="RuntimeSchedule.PublicTrack"/>, Engine-Post — are created by
/// <see cref="RuntimeSchedule.Create"/>.
/// </para>
/// </remarks>
[PublicAPI]
public sealed class Track
{
    /// <summary>The <c>engine</c> tag — marks tracks that hold engine-internal DAGs (Engine-Pre, Engine-Post).</summary>
    public const string EngineTag = "engine";

    private readonly List<Dag> _dags = [];
    private readonly HashSet<string> _tags;

    internal Track(RuntimeSchedule schedule, string name, int orderIndex, IEnumerable<string> tags)
    {
        Schedule = schedule;
        Name = name;
        OrderIndex = orderIndex;
        _tags = new HashSet<string>(tags ?? [], StringComparer.Ordinal);
    }

    /// <summary>The schedule this track belongs to.</summary>
    internal RuntimeSchedule Schedule { get; }

    /// <summary>Track name. Diagnostic only — no engine behaviour keys off it.</summary>
    public string Name { get; }

    /// <summary>
    /// Execution order index. Lower-indexed tracks run to completion before higher-indexed ones begin. Reassigned by <see cref="RuntimeSchedule.DeclareTrack"/>
    /// as app tracks are slotted in, so it always equals the track's position.
    /// </summary>
    public int OrderIndex { get; internal set; }

    /// <summary>The track's open tag set.</summary>
    public IReadOnlyCollection<string> Tags => _tags;

    /// <summary>DAGs declared on this track, in declaration order.</summary>
    public IReadOnlyList<Dag> Dags => _dags;

    /// <summary>True when this track carries the <see cref="EngineTag"/> — it holds engine-internal DAGs not surfaced as user systems.</summary>
    public bool IsEngine => _tags.Contains(EngineTag);

    /// <summary>
    /// Whether a system failing on this track ends the runtime. True for every track by default; engine tracks whose work is not a durability precondition set
    /// it false.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A throw on an engine track latches <c>DagScheduler.IsFenceFailed</c>, and that latch is <b>terminal</b> — <c>ExecuteCallbacks</c> returns early
    /// on every subsequent tick. That is right for the fence: a tick whose fence did not finish leaves cluster pages mutated, dirty and un-logged, which
    /// the checkpoint then persists with no WAL record behind it (rule TP-01a), so continuing to tick layers more of the same on top.
    /// </para>
    /// <para>
    /// It is <b>wrong</b> for engine work with no durability role. Replication writes only its own RAM-only blocks — never checkpointed, never
    /// WAL-logged — so a bug in a replication stage endangers nothing a later tick could compound. Letting it stop the database would make a defect in
    /// the newest, least-proven subsystem strictly more damaging than the same defect in a user system, and would report it to the host as a *fence*
    /// failure, which it is not.
    /// </para>
    /// </remarks>
    internal bool FailureIsTerminal { get; init; } = true;

    /// <summary>True when <paramref name="tag"/> is present in this track's tag set.</summary>
    public bool HasTag(string tag) => _tags.Contains(tag);

    /// <summary>
    /// Declares a new <see cref="Dag"/> on this track. A DAG that declares no named phases gets a single implicit phase — <c>.After()</c> edges suffice to
    /// order a trivial DAG.
    /// </summary>
    /// <param name="name">DAG name — must be unique across the whole schedule.</param>
    public Dag DeclareDag(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Schedule.ThrowIfBuilt();
        var dag = new Dag(this, name);
        _dags.Add(dag);
        return dag;
    }
}
