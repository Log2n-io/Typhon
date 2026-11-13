using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Typhon.Analyzers;

/// <summary>
/// <b>TYPHON016</b> — an <c>await</c> between a <c>Transaction</c>'s creation and its disposal, in the same method (#1653, EP-04).
/// <para>
/// A transaction belongs to the thread that created it: its epoch scope is that thread's. An <c>await</c> inside its scope resumes on another thread in an
/// ASP.NET host, and the transaction's <c>Commit</c>, <c>Rollback</c> and <c>Dispose</c> then throw there. Syntactic and deterministic: the transaction is
/// a local of a type that is or derives from <c>Typhon.Engine.Transaction</c> (or a <c>using</c> statement over such an expression), its scope is its
/// <c>using</c> declaration's statement list (a block, a switch section or top-level statements), its <c>using</c> statement's body, or, for a plain local,
/// the statements up to and including the first that disposes it (<c>tx.Dispose()</c> or <c>tx?.Dispose()</c>, possibly in a <c>finally</c>). An
/// <c>await</c> in a lambda or local function nested there is another method's, and not reported. Not covered: a transaction held in a field or passed in
/// as a parameter, whose disposal is elsewhere.
/// </para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class TransactionAcrossAwaitAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "TYPHON016";

    private const string TransactionFqn = "Typhon.Engine.Transaction";

    private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
        DiagnosticId,
        "'await' inside a transaction's scope",
        "'await' inside the scope of transaction '{0}': the continuation may resume on another thread, where the transaction's Commit, Rollback and "
        + "Dispose throw. End the transaction before awaiting, or create it after.",
        "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:
            "A Typhon transaction is bound to the thread that created it: it holds an epoch scope of that thread, and ending it on another thread would "
            + "unpin the wrong thread and leave the creator pinned for good (rule EP-04). The engine refuses it at the call; this diagnostic finds it at build "
            + "time.",
        helpLinkUri: DiagnosticHelp.For(DiagnosticId));

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var transaction = start.Compilation.GetTypeByMetadataName(TransactionFqn);
            if (transaction == null)
            {
                return;
            }

            start.RegisterSyntaxNodeAction(c => AnalyzeLocalDeclaration(c, transaction), SyntaxKind.LocalDeclarationStatement);
            start.RegisterSyntaxNodeAction(c => AnalyzeUsingStatement(c, transaction), SyntaxKind.UsingStatement);
        });
    }

    private static void AnalyzeUsingStatement(SyntaxNodeAnalysisContext context, INamedTypeSymbol transaction)
    {
        var statement = (UsingStatementSyntax)context.Node;
        if (statement.Statement == null)
        {
            return;
        }

        if (statement.Declaration == null)
        {
            // using (dbe.CreateQuickTransaction()) { ... }: no name, the same scope
            if (statement.Expression != null && IsTransactionType(context.SemanticModel.GetTypeInfo(statement.Expression, context.CancellationToken).Type,
                    transaction))
            {
                ReportAwaits(context, statement.Statement, statement.Expression.ToString());
            }
            return;
        }

        foreach (var variable in statement.Declaration.Variables)
        {
            if (IsTransactionLocal(context, variable, transaction))
            {
                ReportAwaits(context, statement.Statement, variable.Identifier.Text);
            }
        }
    }

    private static void AnalyzeLocalDeclaration(SyntaxNodeAnalysisContext context, INamedTypeSymbol transaction)
    {
        var declaration = (LocalDeclarationStatementSyntax)context.Node;
        IReadOnlyList<StatementSyntax> statements;
        switch (declaration.Parent)
        {
            case BlockSyntax block:
                statements = block.Statements;
                break;
            case SwitchSectionSyntax section:
                statements = section.Statements;
                break;
            case GlobalStatementSyntax { Parent: CompilationUnitSyntax unit }:
                statements = TopLevelStatements(unit);
                break;
            default:
                return;
        }

        var isUsing = declaration.UsingKeyword.IsKind(SyntaxKind.UsingKeyword);
        foreach (var variable in declaration.Declaration.Variables)
        {
            if (!IsTransactionLocal(context, variable, transaction))
            {
                continue;
            }

            var name = variable.Identifier.Text;
            var after = false;
            foreach (var statement in statements)
            {
                if (statement == declaration)
                {
                    after = true;
                    continue;
                }
                if (!after)
                {
                    continue;
                }

                ReportAwaits(context, statement, name);
                if (!isUsing && DisposesLocal(statement, name))
                {
                    break;   // a plain local's scope ends with the statement that disposes it (a try whose finally does, included)
                }
            }
        }
    }

    private static bool IsTransactionLocal(SyntaxNodeAnalysisContext context, VariableDeclaratorSyntax variable, INamedTypeSymbol transaction)
    {
        return context.SemanticModel.GetDeclaredSymbol(variable, context.CancellationToken) is ILocalSymbol local && IsTransactionType(local.Type, transaction);
    }

    private static bool IsTransactionType(ITypeSymbol candidate, INamedTypeSymbol transaction)
    {
        for (var type = candidate as INamedTypeSymbol; type != null; type = type.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(type, transaction))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<StatementSyntax> TopLevelStatements(CompilationUnitSyntax unit)
    {
        var list = new List<StatementSyntax>();
        foreach (var member in unit.Members)
        {
            if (member is GlobalStatementSyntax global)
            {
                list.Add(global.Statement);
            }
        }

        return list;
    }

    /// <summary>The statement disposes <paramref name="name"/> somewhere in it: <c>name.Dispose()</c> or <c>name?.Dispose()</c>, a <c>finally</c>
    /// included; a nested lambda or local function excluded.</summary>
    private static bool DisposesLocal(StatementSyntax statement, string name)
    {
        foreach (var node in statement.DescendantNodesAndSelf(n => n is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)))
        {
            switch (node)
            {
                case InvocationExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Dispose", Expression: IdentifierNameSyntax id }
                } when id.Identifier.Text == name:
                    return true;
                case ConditionalAccessExpressionSyntax
                {
                    Expression: IdentifierNameSyntax target,
                    WhenNotNull: InvocationExpressionSyntax { Expression: MemberBindingExpressionSyntax { Name.Identifier.Text: "Dispose" } }
                } when target.Identifier.Text == name:
                    return true;
            }
        }

        return false;
    }

    private static void ReportAwaits(SyntaxNodeAnalysisContext context, SyntaxNode scope, string name)
    {
        foreach (var node in scope.DescendantNodesAndSelf(n => n is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)))
        {
            Location location = null;
            switch (node)
            {
                case AwaitExpressionSyntax await:
                    location = await.AwaitKeyword.GetLocation();
                    break;
                case ForEachStatementSyntax { AwaitKeyword.RawKind: (int)SyntaxKind.AwaitKeyword } forEach:
                    location = forEach.AwaitKeyword.GetLocation();
                    break;
                case UsingStatementSyntax { AwaitKeyword.RawKind: (int)SyntaxKind.AwaitKeyword } usingStatement:
                    location = usingStatement.AwaitKeyword.GetLocation();
                    break;
                case LocalDeclarationStatementSyntax { AwaitKeyword.RawKind: (int)SyntaxKind.AwaitKeyword } awaitUsing:
                    location = awaitUsing.AwaitKeyword.GetLocation();
                    break;
            }

            if (location != null)
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, location, name));
            }
        }
    }
}
