using JetBrains.Annotations;
using System;

namespace Typhon.Engine;

/// <summary>
/// A phase token in the system scheduler. Phases form a DAG-local total order (see RFC 07 / Q3) — every system belongs to a phase of its DAG, and all systems
/// in phase N complete before any system in phase N+1 of the same DAG.
/// </summary>
/// <remarks>
/// <para>
/// Typhon ships four reusable phase tokens via static fields: <see cref="Input"/>, <see cref="Simulation"/>, <see cref="Output"/>, <see cref="Cleanup"/>.
/// A DAG declares its ordered phase sequence via <see cref="Dag.Phases"/>; user code may reuse these tokens or declare its own.
/// </para>
/// <para>
/// The token wraps a <see cref="string"/> for diagnostic clarity and minimal memory footprint — phases are resolved to integer indices
/// once at <see cref="RuntimeSchedule.Build"/> time, after which the runtime uses the int form everywhere.
/// </para>
/// </remarks>
[PublicAPI]
public readonly struct Phase : IEquatable<Phase>
{
    /// <summary>The phase name — used for equality, hashing, and diagnostic output.</summary>
    public readonly string Name;

    /// <summary>Constructs a phase token. Prefer the static fields (<see cref="Input"/>, etc.) or define your own statics.</summary>
    public Phase(string name) => Name = name;

    // ─── Typhon-shipped default phases ──────────────────────────────
    /// <summary>The Input phase — runs first by default. Reserved for systems that ingest external commands or inputs.</summary>
    public static readonly Phase Input = new("Input");

    /// <summary>The Simulation phase — runs after Input by default. Reserved for the bulk of game-logic / simulation systems.</summary>
    public static readonly Phase Simulation = new("Simulation");

    /// <summary>The Output phase — runs after Simulation by default. Reserved for systems that emit data downstream (rendering, network, persistence).</summary>
    public static readonly Phase Output = new("Output");

    /// <summary>The Cleanup phase — runs last by default. Reserved for systems that finalise tick state (archetype cleanup, index updates, etc.).</summary>
    public static readonly Phase Cleanup = new("Cleanup");

    /// <summary>Ordinal equality on <see cref="Name"/>.</summary>
    /// <param name="other">The phase to compare with.</param>
    /// <returns><c>true</c> when both names are ordinally equal.</returns>
    public bool Equals(Phase other) => string.Equals(Name, other.Name, StringComparison.Ordinal);

    /// <summary>Ordinal equality — <c>true</c> when <paramref name="obj"/> is a <see cref="Phase"/> with an equal <see cref="Name"/>.</summary>
    /// <param name="obj">The object to compare with.</param>
    /// <returns><c>true</c> when <paramref name="obj"/> is an equal <see cref="Phase"/>.</returns>
    public override bool Equals(object obj) => obj is Phase p && Equals(p);

    /// <summary>Ordinal hash of <see cref="Name"/>; <c>0</c> when the name is <c>null</c>.</summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode() => Name?.GetHashCode(StringComparison.Ordinal) ?? 0;

    /// <summary>Ordinal equality operator — see <see cref="Equals(Phase)"/>.</summary>
    /// <param name="a">Left operand.</param>
    /// <param name="b">Right operand.</param>
    /// <returns><c>true</c> when the phases are equal.</returns>
    public static bool operator ==(Phase a, Phase b) => a.Equals(b);

    /// <summary>Ordinal inequality operator — negation of <see cref="Equals(Phase)"/>.</summary>
    /// <param name="a">Left operand.</param>
    /// <param name="b">Right operand.</param>
    /// <returns><c>true</c> when the phases differ.</returns>
    public static bool operator !=(Phase a, Phase b) => !a.Equals(b);

    /// <summary>The phase <see cref="Name"/>, or <c>"&lt;unset&gt;"</c> when the name is <c>null</c>.</summary>
    /// <returns>The phase name, for diagnostic output.</returns>
    public override string ToString() => Name ?? "<unset>";
}
