using Eval;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NUnit.Framework;

namespace EvalChecker;

[TestFixture]
public class T10
{
    [Test]
    public void Runtime_Builds_With_Declared_Writer()
    {
        using var db = Solution.Open(H.NewDbPath("t10"));
        using var rt = Solution.CreateRuntime(db);
        Assert.That(rt.UserSystems.Any(s => s.Access.Writes.Contains(typeof(UnitHealth))), Is.True, "no system declares Writes<UnitHealth>");
    }

    [Test]
    public void Health_Writer_Replicates()
    {
        var writers = H.Sources().All<ClassDeclarationSyntax>()
            .Where(c => c.DescendantNodes().OfType<GenericNameSyntax>().Any(g => g.Identifier.Text == "Writes"
                        && g.TypeArgumentList.Arguments.Any(a => a.ToString().EndsWith("UnitHealth"))))
            .ToList();
        Assert.That(writers, Is.Not.Empty, "no class declares Writes<UnitHealth>");
        foreach (var c in writers)
        {
            bool replicates = c.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Any(i => i.Expression switch
                {
                    MemberAccessExpressionSyntax m => m.Name.Identifier.Text == "Replicate",
                    MemberBindingExpressionSyntax b => b.Name.Identifier.Text == "Replicate",   // ctx.Subscriptions?.Replicate(...)
                    _ => false,
                });
            Assert.That(replicates, Is.True, $"{c.Identifier.Text} writes replicated health but never calls Replicate: clients never see it");
        }
    }
}
