using System.Diagnostics;
using NUnit.Framework;

namespace EvalChecker;

[TestFixture]
public class T07
{
    [Test]
    public void V1_Database_Opens_With_Mana_50()
    {
        var path = H.NewDbPath("t07");
        var seed = Environment.GetEnvironmentVariable("EVAL_T07_SEED") ?? throw new InvalidOperationException("EVAL_T07_SEED not set");
        var psi = new ProcessStartInfo("dotnet", $"\"{seed}\" \"{path}\"") { RedirectStandardOutput = true, RedirectStandardError = true };
        using (var p = Process.Start(psi))
        {
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit(60_000);
            Assert.That(p.ExitCode, Is.Zero, "v1 seed failed: " + err);
        }

        using var db = Solution.Open(path);
        var heroes = Solution.AllHeroes(db);
        Assert.That(heroes.Count, Is.EqualTo(100), "heroes lost");
        var stats = heroes.Select(h => Solution.ReadHero(db, h)).ToList();
        var expected = Enumerable.Range(1, 100).Select(i => (i * 10, i)).ToHashSet();
        Assert.That(stats.Select(s => (s.Hp, s.Armor)).ToHashSet().SetEquals(expected), Is.True, "Hp/Armor not preserved");
        Assert.That(stats.Count(s => s.Mana != 50), Is.Zero, $"{stats.Count(s => s.Mana != 50)} heroes without Mana = 50");
    }

    [Test]
    public void Reopen_Keeps_Mana()
    {
        var path = H.NewDbPath("t07b");
        var seed = Environment.GetEnvironmentVariable("EVAL_T07_SEED");
        using (var p = Process.Start(new ProcessStartInfo("dotnet", $"\"{seed}\" \"{path}\"")))
        {
            p.WaitForExit(60_000);
            Assert.That(p.ExitCode, Is.Zero);
        }
        using (var db = Solution.Open(path))
        {
            Assert.That(Solution.AllHeroes(db).Count, Is.EqualTo(100));
        }
        using (var db = Solution.Open(path))
        {
            var stats = Solution.AllHeroes(db).Select(h => Solution.ReadHero(db, h)).ToList();
            Assert.That(stats.Count(s => s.Mana != 50), Is.Zero, "Mana changed on a second open of the migrated database");
        }
    }
}
