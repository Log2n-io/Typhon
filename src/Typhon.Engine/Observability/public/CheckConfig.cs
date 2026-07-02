// unset

using JetBrains.Annotations;
using Microsoft.Extensions.Configuration;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine;

/// <summary>
/// Runtime gate for Typhon's user-facing correctness checks — the "strict mode" (issue #422).
///
/// <para>
/// The Release NuGet strips every <c>Debug.Assert</c> / <c>#if DEBUG</c> check, so users get no diagnostics when they
/// misuse the API. The valuable, user-facing checks are converted to run behind this gate: <c>CheckConfig.Require(Enabled, …)</c>.
/// Mirroring <see cref="TelemetryConfig"/>, the gate is a <c>static readonly bool</c> so the JIT dead-code-eliminates the
/// check path entirely when strict mode is off (the Release default) — zero cost on the hot path.
/// </para>
///
/// <para>
/// <b>Off by default, everywhere.</b> Enable deliberately when diagnosing, via <c>Typhon:Checks:Enabled</c> in
/// <c>typhon.telemetry.json</c> or the <c>TYPHON__CHECKS__ENABLED</c> environment variable. There is no build-config
/// auto-detection and no runtime setter (a mutable field would defeat the JIT fold — restart to reconfigure).
/// </para>
///
/// <para>
/// <b>Startup:</b> call <see cref="EnsureInitialized"/> once before hot paths JIT (wired in the engine's module
/// initializer, next to <see cref="TelemetryConfig.EnsureInitialized"/>). Configuration is read once from the same merged
/// source as <see cref="TelemetryConfig"/> (<c>typhon.telemetry.json</c> in cwd, then next to the assembly, then env vars).
/// </para>
/// </summary>
[PublicAPI]
public static class CheckConfig
{
    /// <summary>
    /// Master strict-mode switch (default <c>false</c>). When <c>true</c>, all the cheap user-facing misuse checks run.
    /// Reads <c>Typhon:Checks:Enabled</c> / <c>TYPHON__CHECKS__ENABLED</c>.
    /// </summary>
    public static readonly bool Enabled;

    /// <summary>
    /// Separate opt-in (default <c>false</c>) for the one costly check — <c>SystemAccessValidator.AssertWrite&lt;T&gt;</c> and its
    /// <c>Enter/LeaveSystem</c> descriptor-stack infrastructure — so enabling strict mode does not force a <c>HashSet</c> lookup
    /// on every <c>Write&lt;T&gt;()</c>. Effective value is <c>Enabled AND Typhon:Checks:DeclaredAccess</c>.
    /// </summary>
    public static readonly bool DeclaredAccessActive;

    /// <summary>
    /// Number of <see cref="Record(bool, bool, ref CheckMessageHandler)"/> violations flagged this process (latch-safe checks
    /// that record instead of throwing). Diagnostic counter; read via <see cref="Interlocked.Read"/>. Internal — not a public
    /// API surface (mirrors <c>SpatialRTreeDiagnostics.DfsStackOverflowCount</c>).
    /// </summary>
    internal static long RecordedViolationCount;

    static CheckConfig()
    {
        // Reuse the already-merged configuration built by TelemetryConfig (cwd json → assembly-dir json → env vars). Accessing
        // it triggers TelemetryConfig's static ctor; there is no cycle (TelemetryConfig never references CheckConfig).
        var config = TelemetryConfig.Configuration;

        Enabled = ReadBool(config, "Typhon:Checks:Enabled", false);
        DeclaredAccessActive = Enabled && ReadBool(config, "Typhon:Checks:DeclaredAccess", false);
    }

    private static bool ReadBool(IConfiguration config, string key, bool defaultValue)
    {
        var v = config[key];
        if (string.IsNullOrEmpty(v))
        {
            return defaultValue;
        }
        return bool.TryParse(v, out var b) ? b : defaultValue;
    }

    /// <summary>
    /// Forces early initialization so the JIT sees the final <c>static readonly</c> gate values before compiling hot paths.
    /// Call once at startup. <see cref="MethodImplOptions.NoInlining"/> guarantees the call (and thus the static ctor) runs.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void EnsureInitialized() => _ = Enabled;

    /// <summary>
    /// Strict-mode assertion: when <paramref name="enabled"/> is set and <paramref name="condition"/> is false, throw an
    /// <see cref="System.InvalidOperationException"/> (via <c>ThrowHelper</c>). The <paramref name="message"/> is built <b>only</b>
    /// on failure — the interpolated-string handler skips all formatting when the check passes or strict mode is off, so a
    /// passing call in strict mode allocates nothing. Pass <see cref="Enabled"/> (or a sub-gate) as <paramref name="enabled"/>
    /// so the JIT folds the whole call to a no-op when the gate is off.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Require(bool enabled, bool condition, [InterpolatedStringHandlerArgument("enabled", "condition")] ref CheckMessageHandler message)
    {
        if (enabled && !condition)
        {
            ThrowHelper.ThrowInvalidOp(message.ToStringAndClear());
        }
    }

    /// <summary>
    /// Latch-safe strict-mode check: like <see cref="Require"/> but <b>never throws</b> — on failure it increments
    /// <see cref="RecordedViolationCount"/>. Use where a throw is unsafe (e.g. reachable while holding an OLC latch, where an
    /// exception would leak the latch and deadlock). The message is built only on failure.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Record(bool enabled, bool condition, [InterpolatedStringHandlerArgument("enabled", "condition")] ref CheckMessageHandler message)
    {
        if (enabled && !condition)
        {
            Interlocked.Increment(ref RecordedViolationCount);
            // Never throw — Record is the latch-safe primitive (an exception under an OLC latch would leak it and deadlock).
            // ToStringAndClear releases the handler's pooled buffer; the string is discarded (Record counts, it does not log).
            // NOTE: message *formatting* runs at the call site (handler construction), so under a latch the caller must use a
            // non-throwing message (a literal or safe interpolation), same as SpatialRTreeDiagnostics uses a plain label.
            try
            {
                _ = message.ToStringAndClear();
            }
            catch
            {
                // Swallow — the violation is already recorded in the counter.
            }
        }
    }
}

/// <summary>
/// Interpolated-string handler for <see cref="CheckConfig.Require"/> / <see cref="CheckConfig.Record"/>. Formats the message
/// <b>only</b> when the check is going to fire (<c>enabled &amp;&amp; !condition</c>); otherwise every append is skipped and no
/// string is allocated. Mirrors the pattern of <see cref="System.Diagnostics.Debug.Assert(bool, string)"/>'s modern overload.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
[InterpolatedStringHandler]
[PublicAPI]
public ref struct CheckMessageHandler
{
    private DefaultInterpolatedStringHandler _inner;
    private readonly bool _active;

    /// <summary>Constructed by the compiler at the call site; <paramref name="shouldAppend"/> gates all formatting work.</summary>
    public CheckMessageHandler(int literalLength, int formattedCount, bool enabled, bool condition, out bool shouldAppend)
    {
        _active = enabled && !condition;
        shouldAppend = _active;
        _inner = _active ? new DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
    }

    /// <summary>Appends a literal fragment (only when the check is firing).</summary>
    public void AppendLiteral(string value)
    {
        if (_active)
        {
            _inner.AppendLiteral(value);
        }
    }

    /// <summary>Appends a formatted value (only when the check is firing).</summary>
    public void AppendFormatted<T>(T value)
    {
        if (_active)
        {
            _inner.AppendFormatted(value);
        }
    }

    /// <summary>Appends a string value (only when the check is firing).</summary>
    public void AppendFormatted(string value)
    {
        if (_active)
        {
            _inner.AppendFormatted(value);
        }
    }

    /// <summary>Appends a formatted value with a format specifier (e.g. <c>{x:X}</c>), only when the check is firing.</summary>
    public void AppendFormatted<T>(T value, string format)
    {
        if (_active)
        {
            _inner.AppendFormatted(value, format);
        }
    }

    /// <summary>Appends a formatted value with alignment (e.g. <c>{x,5}</c>), only when the check is firing.</summary>
    public void AppendFormatted<T>(T value, int alignment)
    {
        if (_active)
        {
            _inner.AppendFormatted(value, alignment);
        }
    }

    /// <summary>Appends a formatted value with alignment and format (e.g. <c>{x,5:X}</c>), only when the check is firing.</summary>
    public void AppendFormatted<T>(T value, int alignment, string format)
    {
        if (_active)
        {
            _inner.AppendFormatted(value, alignment, format);
        }
    }

    /// <summary>Appends a string with alignment, only when the check is firing.</summary>
    public void AppendFormatted(string value, int alignment, string format = null)
    {
        if (_active)
        {
            _inner.AppendFormatted(value, alignment, format);
        }
    }

    /// <summary>Returns the built message (empty when the check did not fire) and resets the handler.</summary>
    internal string ToStringAndClear() => _active ? _inner.ToStringAndClear() : string.Empty;
}
