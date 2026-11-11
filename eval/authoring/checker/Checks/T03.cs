using NUnit.Framework;

namespace EvalChecker;

[TestFixture]
public class T03
{
    [Test]
    public void Sequential_Duplicates_Refused()
    {
        using var db = Solution.Open(H.NewDbPath("t03"));
        Assert.That(Solution.TryCreateAccount(db, "alice"), Is.True);
        Assert.That(Solution.TryCreateAccount(db, "alice"), Is.False, "duplicate accepted");
        Assert.That(Solution.TryCreateAccount(db, "bob"), Is.True);
        Assert.That(Solution.CountAccounts(db), Is.EqualTo(2));
    }

    [Test]
    public void Concurrent_SameLogin_ExactlyOne()
    {
        using var db = Solution.Open(H.NewDbPath("t03"));
        for (int round = 0; round < 20; round++)
        {
            var login = "user" + round;
            int created = 0;
            var start = new Barrier(16);
            var threads = Enumerable.Range(0, 16).Select(_ => new Thread(() =>
            {
                start.SignalAndWait();
                if (Solution.TryCreateAccount(db, login))
                {
                    Interlocked.Increment(ref created);
                }
            })).ToList();
            Exception failure = null;
            foreach (var t in threads)
            {
                t.UnsafeStart();
            }
            foreach (var t in threads)
            {
                t.Join();
            }
            Assert.That(failure, Is.Null);
            Assert.That(created, Is.EqualTo(1), $"round {round}: {created} accounts created for one login");
        }
        Assert.That(Solution.CountAccounts(db), Is.EqualTo(20));
    }
}
