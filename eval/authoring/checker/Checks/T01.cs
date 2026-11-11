using NUnit.Framework;
using Typhon.Engine;

namespace EvalChecker;

[TestFixture]
public class T01
{
    [Test]
    public void RoundTrip_SameSession()
    {
        using var db = Solution.Open(H.NewDbPath("t01"));
        var id = Solution.CreatePlayer(db, "Ann", 4);
        Solution.LevelUp(db, id);
        Assert.That(Solution.GetPlayer(db, id), Is.EqualTo(("Ann", 5)));
    }

    [Test]
    public void Survives_Reopen()
    {
        var path = H.NewDbPath("t01");
        var longName = new string('x', 40);
        EntityId a, b, c;
        using (var db = Solution.Open(path))
        {
            a = Solution.CreatePlayer(db, "Ann", 1);
            b = Solution.CreatePlayer(db, longName, 7);
            c = Solution.CreatePlayer(db, "Cid", 3);
            Solution.LevelUp(db, b);
            Solution.LevelUp(db, b);
        }

        using (var db = Solution.Open(path))
        {
            Assert.That(Solution.GetPlayer(db, a), Is.EqualTo(("Ann", 1)));
            Assert.That(Solution.GetPlayer(db, b), Is.EqualTo((longName, 9)), "40-character name or level lost");
            Assert.That(Solution.GetPlayer(db, c), Is.EqualTo(("Cid", 3)));
        }
    }

    [Test]
    public void LevelUp_Concurrent_NoLostUpdate()
    {
        using var db = Solution.Open(H.NewDbPath("t01"));
        var id = Solution.CreatePlayer(db, "Bob", 0);
        Parallel.For(0, 200, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ => Solution.LevelUp(db, id));
        Assert.That(Solution.GetPlayer(db, id).Level, Is.EqualTo(200), "level changes must be atomic");
    }
}
