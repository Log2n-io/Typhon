using System;
using System.Collections.Generic;
using System.IO;
using AntHill.Core;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AntHill.Harness;

/// <summary>Thrown when a scenario file is missing, malformed, or fails validation.</summary>
public sealed class ScenarioException : Exception
{
    public ScenarioException(string message) : base(message)
    {
    }

    public ScenarioException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>
/// Loads and validates a <see cref="Scenario"/> from a YAML file. Unknown keys, malformed YAML, and any validation failure are reported as a
/// <see cref="ScenarioException"/> with a clear message — the harness turns that into a non-zero exit code.
/// </summary>
public static class ScenarioLoader
{
    /// <summary>World size is fixed in v1 — see the design doc's "Scenario-driven config" note.</summary>
    public const int FixedWorldSize = 20_000;

    /// <summary>Loads and validates the scenario at <paramref name="path"/>.</summary>
    public static Scenario Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new ScenarioException($"Scenario file not found: '{path}'.");
        }

        Scenario scenario;
        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .Build();
            scenario = deserializer.Deserialize<Scenario>(File.ReadAllText(path));
        }
        catch (YamlException ex)
        {
            // Covers both syntax errors and unmatched (typo'd) keys — the deserializer is strict.
            throw new ScenarioException($"Malformed scenario YAML in '{path}': {ex.Message}", ex);
        }

        if (scenario == null)
        {
            throw new ScenarioException($"Scenario file '{path}' is empty.");
        }

        Validate(scenario, path);
        return scenario;
    }

    /// <summary>Parses a tier-mode string (<c>camera</c> / <c>uniform-t0</c>, case-insensitive).</summary>
    public static bool TryParseTierMode(string raw, out TierMode mode)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case "camera":
                mode = TierMode.Camera;
                return true;
            case "uniform-t0":
                mode = TierMode.UniformT0;
                return true;
            default:
                mode = TierMode.Camera;
                return false;
        }
    }

    private static void Validate(Scenario s, string path)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(s.Name))
        {
            errors.Add("'name' is required.");
        }

        if (s.Ants <= 0)
        {
            errors.Add($"'ants' must be greater than 0 (was {s.Ants}).");
        }

        if (s.World != FixedWorldSize)
        {
            errors.Add($"'world' must be {FixedWorldSize} — world size is fixed in v1 (was {s.World}).");
        }

        if (s.Workers == null || s.Workers.Count == 0)
        {
            errors.Add("'workers' must list at least one worker count.");
        }
        else
        {
            foreach (var w in s.Workers)
            {
                if (w <= 0)
                {
                    errors.Add($"'workers' entries must be greater than 0 (found {w}).");
                }
            }
        }

        var hasDuration = s.Duration.HasValue;
        var hasTicks = s.Ticks.HasValue;
        if (hasDuration && hasTicks)
        {
            errors.Add("'duration' and 'ticks' are mutually exclusive — set only one.");
        }
        if (!hasDuration && !hasTicks)
        {
            errors.Add("one of 'duration' or 'ticks' is required.");
        }
        if (hasDuration && s.Duration.Value <= 0)
        {
            errors.Add($"'duration' must be greater than 0 (was {s.Duration.Value}).");
        }
        if (hasTicks && s.Ticks.Value <= 0)
        {
            errors.Add($"'ticks' must be greater than 0 (was {s.Ticks.Value}).");
        }

        var tierModeRaw = s.Simulation?.TierMode ?? "camera";
        if (!TryParseTierMode(tierModeRaw, out _))
        {
            errors.Add($"'simulation.tierMode' must be 'camera' or 'uniform-t0' (was '{tierModeRaw}').");
        }

        if (errors.Count > 0)
        {
            throw new ScenarioException(
                $"Scenario '{path}' failed validation:{Environment.NewLine}  - "
                + string.Join($"{Environment.NewLine}  - ", errors));
        }
    }
}
