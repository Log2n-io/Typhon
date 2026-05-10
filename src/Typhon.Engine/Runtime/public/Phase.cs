using JetBrains.Annotations;
using System;

namespace Typhon.Engine;

/// <summary>
/// A phase token in the system scheduler. Phases form a user-defined total order (see RFC 07 / Q3) — every system
/// belongs to a phase, and all systems in phase N complete before any system in phase N+1 starts.
/// </summary>
/// <remarks>
/// <para>
/// Typhon ships four default phases via static fields: <see cref="Input"/>, <see cref="Simulation"/>, <see cref="Output"/>, <see cref="Cleanup"/>.
/// User code extends the set by declaring additional phases as static fields in its own class and listing them
/// (in order) in <see cref="RuntimeOptions.Phases"/>.
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

    public bool Equals(Phase other) => string.Equals(Name, other.Name, StringComparison.Ordinal);

    public override bool Equals(object obj) => obj is Phase p && Equals(p);

    public override int GetHashCode() => Name?.GetHashCode(StringComparison.Ordinal) ?? 0;

    public static bool operator ==(Phase a, Phase b) => a.Equals(b);

    public static bool operator !=(Phase a, Phase b) => !a.Equals(b);

    public override string ToString() => Name ?? "<unset>";
}
