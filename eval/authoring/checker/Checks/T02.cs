using Microsoft.CodeAnalysis.CSharp.Syntax;
using NUnit.Framework;
using Typhon.Engine;

namespace EvalChecker;

[TestFixture]
public class T02
{
    [Test]
    public void Results_Correct()
    {
        using var db = Solution.Open(H.NewDbPath("t02"));
        var expected = new HashSet<EntityId>();
        for (int i = 0; i < 3000; i++)
        {
            int level = 1 + (i % 100);
            var id = Solution.AddPlayer(db, "p" + i, level);
            if (level >= 95)
            {
                expected.Add(id);
            }
        }
        var got = Solution.FindAtLeastLevel(db, 95);
        Assert.That(got.Count, Is.EqualTo(expected.Count), "count");
        Assert.That(new HashSet<EntityId>(got).SetEquals(expected), Is.True, "members");
        Assert.That(Solution.FindAtLeastLevel(db, 101), Is.Empty);
        Assert.That(Solution.FindAtLeastLevel(db, 1).Count, Is.EqualTo(3000));
    }

    [Test]
    public void Level_Has_NonUnique_Index()
    {
        var fields = H.Sources().All<FieldDeclarationSyntax>()
            .Where(f => f.Declaration.Variables.Any(v => v.Identifier.Text == "Level"))
            .ToList();
        Assert.That(fields, Is.Not.Empty, "no field named Level");
        bool indexed = fields.Any(f => f.AttributeLists.SelectMany(l => l.Attributes)
            .Any(a => (a.Name.ToString() is "Index" or "IndexAttribute") && a.ToString().Replace(" ", "").Contains("AllowMultiple=true")));
        Assert.That(indexed, Is.True, "Level must carry [Index(AllowMultiple = true)]");
    }

    [Test]
    public void Query_Uses_Index_Not_Scan()
    {
        var method = H.Sources().All<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == "FindAtLeastLevel");
        Assert.That(method, Is.Not.Null);
        var calls = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Select(i => i.Expression switch
            {
                MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
                _ => null,
            })
            .Where(n => n != null)
            .ToList();
        Assert.That(calls, Does.Contain("WhereField"), "FindAtLeastLevel must query through WhereField (the index)");
        Assert.That(calls, Does.Not.Contain("Where"), "Where(...) is a per-entity scan");
    }
}
