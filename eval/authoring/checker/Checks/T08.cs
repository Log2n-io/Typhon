using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NUnit.Framework;
using Typhon.Engine;

namespace EvalChecker;

[TestFixture]
public class T08
{
    [Test]
    public void Concurrent_Deposits_None_Lost()
    {
        using var db = Solution.Open(H.NewDbPath("t08"));
        var accounts = Enumerable.Range(0, 5).Select(_ => Solution.CreateAccount(db)).ToArray();
        var tasks = new List<Task>();
        for (int i = 0; i < 400; i++)
        {
            var acc = accounts[i % accounts.Length];
            int n = i;
            tasks.Add(Task.Run(() => Solution.DepositAsync(db, acc, 1, async () =>
            {
                if (n % 2 == 0)
                {
                    await Task.Yield();
                }
                else
                {
                    await Task.Delay(1);
                }
            })));
        }
        Assert.DoesNotThrowAsync(() => Task.WhenAll(tasks));
        foreach (var a in accounts)
        {
            Assert.That(Solution.GetBalance(db, a), Is.EqualTo(80), "deposits lost or duplicated");
        }
    }

    [Test]
    public void No_Transaction_Held_Across_Await()
    {
        var violations = new List<string>();
        foreach (var tree in H.Sources())
        {
            var root = tree.GetRoot();
            foreach (var create in root.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(IsTransactionCreation))
            {
                var (start, end) = Scope(create);
                foreach (var aw in root.DescendantNodes().OfType<AwaitExpressionSyntax>())
                {
                    if (aw.SpanStart > start && aw.SpanStart < end && SameMethod(aw, create))
                    {
                        violations.Add($"{tree.FilePath}:{aw.GetLocation().GetLineSpan().StartLinePosition.Line + 1}");
                    }
                }
            }
        }
        Assert.That(violations, Is.Empty, "await while a transaction is open: " + string.Join(", ", violations));
    }

    private static bool IsTransactionCreation(InvocationExpressionSyntax inv)
    {
        var name = inv.Expression switch
        {
            MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
            IdentifierNameSyntax i => i.Identifier.Text,
            _ => "",
        };
        return name.StartsWith("Create") && (name.EndsWith("Transaction") || name == "CreateUnitOfWork");
    }

    private static bool SameMethod(SyntaxNode a, SyntaxNode b)
        => a.Ancestors().FirstOrDefault(n => n is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax)
           == b.Ancestors().FirstOrDefault(n => n is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax);

    // The source span over which the created transaction is alive.
    private static (int Start, int End) Scope(InvocationExpressionSyntax create)
    {
        var usingStmt = create.Ancestors().OfType<UsingStatementSyntax>().FirstOrDefault(u =>
            (u.Declaration != null && u.Declaration.Span.Contains(create.Span)) || (u.Expression != null && u.Expression.Span.Contains(create.Span)));
        if (usingStmt != null)
        {
            return (create.SpanStart, usingStmt.Span.End);
        }

        var local = create.Ancestors().OfType<LocalDeclarationStatementSyntax>().FirstOrDefault();
        if (local != null)
        {
            if (local.UsingKeyword.IsKind(SyntaxKind.UsingKeyword))
            {
                var block = local.Parent;
                return (create.SpanStart, block.Span.End);
            }
            var name = local.Declaration.Variables.First().Identifier.Text;
            var method = create.Ancestors().FirstOrDefault(n => n is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax);
            int last = method?.DescendantNodes().OfType<IdentifierNameSyntax>().Where(i => i.Identifier.Text == name).Select(i => i.Span.End).DefaultIfEmpty(create.Span.End).Max()
                       ?? create.Span.End;
            return (create.SpanStart, last);
        }

        return (create.SpanStart, create.Span.End);
    }
}
