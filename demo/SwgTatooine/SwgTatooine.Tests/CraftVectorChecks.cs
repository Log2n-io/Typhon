using System;
using System.Globalization;
using System.IO;
using System.Text;
using NUnit.Framework;
using SwgTatooine.Server;

namespace SwgTatooine.Tests;

/// <summary>
/// C2-1's shared vectors: the server's schematics (<c>/swg/schematics</c>) and, for every schematic, its lines' ceilings and values over a spread of inputs,
/// written into the client's test data — where <c>craft.test.ts</c> checks its twin computes the same. A stale file fails here and is rewritten: commit it.
/// </summary>
[TestFixture]
internal sealed class CraftVectorChecks
{
    [Test]
    public void TheClientsVectors_AreTheServers()
    {
        var dir = ClientTestData();
        Assume.That(dir, Is.Not.Null, "no client checkout beside the tests");
        var schematics = CraftEndpoints.SchematicsJson();
        var vectors = Vectors();
        var stale = Rewrite(Path.Combine(dir, "schematics.json"), schematics) | Rewrite(Path.Combine(dir, "craft-vectors.json"), vectors);
        Assert.That(stale, Is.False, "the client's craft vectors were stale and have been rewritten: run the client's craft.test.ts and commit them");
    }

    // Per schematic, three input sets — every Mk at 1, 3 and 5, parts at grades .2, .5 and .9, segments padded then composite — and per line its ceiling
    // (millionths) and its value at f = .6 with no add-on and with a Mk II one.
    private static string Vectors()
    {
        var inv = CultureInfo.InvariantCulture;
        var j = new StringBuilder(65_536);
        j.Append("{\"vectors\":[");
        var first = true;
        Span<int> q = stackalloc int[Crafting.MaxLines];
        foreach (var s in Schematics.Every)
        {
            for (var set = 0; set < 3; set++)
            {
                var mk = (byte)(1 + (2 * set));
                var grade = set switch { 0 => 0.2, 1 => 0.5, _ => 0.9 };
                var segment = set == 2 ? Schematics.CompositeSegment : Schematics.PaddedSegment;
                var inputs = new Schematics.Input[s.Slots.Length];
                for (var k = 0; k < s.Slots.Length; k++)
                {
                    var slot = s.Slots[k];
                    if (slot.Kind == SlotKind.Part)
                    {
                        var (bk, be) = slot.Template == 0 ? Schematics.BiasOf(segment) : (0d, 0d);
                        inputs[k] = new Schematics.Input(slot.Template == 0 ? segment : slot.Template, 0, grade, bk, be);
                    }
                    else
                    {
                        inputs[k] = new Schematics.Input(slot.Template, mk, 0, 0, 0);
                    }
                }

                Crafting.Ceilings(s, inputs, q);
                j.Append(first ? "{" : ",{").Append("\"schematic\":").Append(s.Id).Append(",\"inputs\":[");
                first = false;
                for (var k = 0; k < inputs.Length; k++)
                {
                    var i = inputs[k];
                    j.Append(k == 0 ? "{" : ",{")
                        .Append("\"template\":").Append(i.Template)
                        .Append(",\"mk\":").Append(i.Mk)
                        .Append(",\"grade\":").Append(i.Grade.ToString("R", inv))
                        .Append(",\"biasKinetic\":").Append(i.BiasKinetic.ToString("R", inv))
                        .Append(",\"biasEnergy\":").Append(i.BiasEnergy.ToString("R", inv)).Append('}');
                }

                j.Append("],\"lines\":[");
                for (var l = 0; l < s.Lines.Length; l++)
                {
                    j.Append(l == 0 ? "{" : ",{")
                        .Append("\"q\":").Append(q[l])
                        .Append(",\"value\":").Append(Crafting.ValueOf(s, l, q[l], 600_000, 0).ToString("R", inv))
                        .Append(",\"valueEnhanced\":").Append(Crafting.ValueOf(s, l, q[l], 600_000, 2).ToString("R", inv)).Append('}');
                }

                j.Append("]}");
            }
        }

        return j.Append("]}").ToString();
    }

    private static bool Rewrite(string path, string content)
    {
        var current = File.Exists(path) ? File.ReadAllText(path) : null;
        if (current == content)
        {
            return false;
        }

        File.WriteAllText(path, content);
        return true;
    }

    private static string ClientTestData()
    {
        for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "SwgTatooine.Client", "test");
            if (Directory.Exists(candidate))
            {
                var data = Path.Combine(candidate, "data");
                Directory.CreateDirectory(data);
                return data;
            }
        }

        return null;
    }
}
