using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// How much disk a database's profiling captures may occupy, stored with the data as <c>{name}.typhon/profilings/retention.json</c> (#616, design D-6).
/// </summary>
/// <remarks>
/// <para>
/// <b>Policy travels with the data; the writer enforces it.</b> The Workbench is the natural editor of this file, but it is not always present — a game
/// server, a CI box, a customer deployment writes captures with no Workbench installed. A budget only the Workbench honoured would leave those disks filling
/// exactly as before; the surprise would just have moved. So whoever writes a capture prunes first, from this file, with no tooling required.
/// </para>
/// <para>
/// <b>Pinned captures are counted in the budget and never evicted.</b> Exempting them from the total would make the number a lie precisely when it matters:
/// pin 200 GB and the dashboard still reads "18 of 20 GB" while the disk fills. Counting them keeps the figure honest and the protection identical.
/// </para>
/// </remarks>
[PublicAPI]
public sealed record RetentionPolicy
{
    /// <summary>Default disk budget when a database has no policy file — 20 GiB, the figure the design's worked example uses.</summary>
    public const long DefaultBudgetBytes = 20L * 1024 * 1024 * 1024;

    /// <summary>Default number of newest captures kept regardless of budget.</summary>
    public const int DefaultKeepLatest = 10;

    /// <summary>File name of the policy inside <c>profilings/</c>.</summary>
    public const string FileName = "retention.json";

    /// <summary>Total bytes the captures in <c>profilings/</c> may occupy, pinned ones included. Non-positive disables eviction.</summary>
    public long BudgetBytes { get; init; } = DefaultBudgetBytes;

    /// <summary>
    /// How many of the newest captures survive even when that exceeds <see cref="BudgetBytes"/>. A floor, not a target: the most recent capture is the one
    /// someone is most likely to be about to look at, and a budget that could delete it would make profiling unreliable rather than bounded.
    /// </summary>
    public int KeepLatest { get; init; } = DefaultKeepLatest;

    /// <summary>
    /// Capture <b>file names</b> (not paths) that must never be evicted. Names keep the directory relocatable — copying or moving a bundle preserves the pins.
    /// Unknown entries are ignored, so a pin outlives the capture it named without becoming an error.
    /// </summary>
    public string[] Pinned { get; init; } = [];

    /// <summary>The built-in policy used when a database has no <c>retention.json</c>.</summary>
    public static RetentionPolicy Default => new();

    /// <summary>True when <paramref name="fileName"/> is pinned. Case-insensitive: the file systems this runs on mostly are.</summary>
    public bool IsPinned(string fileName)
    {
        var pinned = Pinned;
        if (pinned == null || fileName == null)
        {
            return false;
        }
        for (var i = 0; i < pinned.Length; i++)
        {
            if (string.Equals(pinned[i], fileName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Reads the policy from a <c>profilings/</c> directory, falling back to <see cref="Default"/> when the file is absent, empty or unreadable.
    /// </summary>
    /// <remarks>
    /// <b>Never throws.</b> A profiling session must not fail to start because a retention file was hand-edited badly — the capture is the valuable thing and
    /// the policy is advice about disk space. <paramref name="malformedReason"/> is non-null when the fallback was taken for a reason worth surfacing (as
    /// opposed to the file simply not existing yet), so the caller can log it once.
    /// </remarks>
    /// <param name="profilingsDirectory">The database's <c>profilings/</c> directory.</param>
    /// <param name="malformedReason">Receives why the default was substituted, or <c>null</c> when the file was read successfully or was simply absent.</param>
    public static RetentionPolicy Read(string profilingsDirectory, out string malformedReason)
    {
        malformedReason = null;
        if (string.IsNullOrEmpty(profilingsDirectory))
        {
            return Default;
        }

        var path = Path.Combine(profilingsDirectory, FileName);
        if (!File.Exists(path))
        {
            return Default;
        }

        try
        {
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                malformedReason = "the file is empty";
                return Default;
            }

            var parsed = JsonSerializer.Deserialize(json, RetentionPolicyJsonContext.Default.RetentionPolicy);
            if (parsed == null)
            {
                malformedReason = "the file contains JSON null";
                return Default;
            }

            // Normalise rather than reject: a hand-edited negative KeepLatest means "none", not "fail the capture".
            return parsed with
            {
                KeepLatest = Math.Max(0, parsed.KeepLatest),
                Pinned = parsed.Pinned ?? [],
            };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            malformedReason = $"{ex.GetType().Name}: {ex.Message}";
            return Default;
        }
    }

    /// <summary>Writes the policy into a <c>profilings/</c> directory, creating it if needed.</summary>
    /// <param name="profilingsDirectory">The database's <c>profilings/</c> directory.</param>
    public void Write(string profilingsDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(profilingsDirectory);
        Directory.CreateDirectory(profilingsDirectory);
        var json = JsonSerializer.Serialize(this, RetentionPolicyJsonContext.Default.RetentionPolicy);
        File.WriteAllText(Path.Combine(profilingsDirectory, FileName), json);
    }
}

/// <summary>
/// Source-generated serialization for <see cref="RetentionPolicy"/>. Generated rather than reflection-based so this file does not become an IL2026 to find
/// later when the engine is made trim/AOT-clean (#409).
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RetentionPolicy))]
internal sealed partial class RetentionPolicyJsonContext : JsonSerializerContext;
