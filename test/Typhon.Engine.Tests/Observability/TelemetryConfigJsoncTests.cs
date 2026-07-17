using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using System.IO;

namespace Typhon.Engine.Tests.Observability;

/// <summary>
/// Locks in the guarantee that the telemetry config loader — Microsoft.Extensions.Configuration.Json's
/// <c>AddJsonFile</c>, used by <see cref="TelemetryConfig"/>'s <c>BuildConfiguration</c> — accepts JSONC:
/// line/block comments and trailing commas. The generated <c>typhon.telemetry.template.jsonc</c> relies on
/// this so it loads as-is without stripping its explanatory comments. If a future change swaps to a stricter
/// parser, this test fails loudly instead of the template silently breaking at a user's site.
/// </summary>
[TestFixture]
public class TelemetryConfigJsoncTests
{
    [Test]
    public void AddJsonFile_AcceptsCommentsAndTrailingCommas()
    {
        const string jsonc = """
        {
            // line comment — mirrors what the generated template emits
            "Typhon": {
                "Profiler": {
                    /* block comment */
                    "Enabled": true,
                    "Concurrency": { "Enabled": false }, // trailing comma follows
                },
            },
        }
        """;

        var path = Path.Combine(Path.GetTempPath(), $"typhon-jsonc-{System.Guid.NewGuid():N}.json");
        File.WriteAllText(path, jsonc);
        try
        {
            var config = new ConfigurationBuilder()
                .AddJsonFile(path, optional: false, reloadOnChange: false)
                .Build();

            Assert.Multiple(() =>
            {
                Assert.That(bool.Parse(config["Typhon:Profiler:Enabled"]), Is.True, "comment-adjacent value should parse");
                Assert.That(bool.Parse(config["Typhon:Profiler:Concurrency:Enabled"]), Is.False, "value after a trailing comma should parse");
            });
        }
        finally
        {
            File.Delete(path);
        }
    }
}
