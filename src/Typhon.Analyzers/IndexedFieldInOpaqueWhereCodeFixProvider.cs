using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Typhon.Analyzers;

/// <summary>
/// Rewrites <c>Where&lt;T&gt;(lambda)</c> to <c>WhereField&lt;T&gt;(lambda)</c> for TYPHON017, when the analyzer marked the predicate as a shape
/// <c>WhereField</c> accepts. The lambda is kept as written: it converts to <c>Expression&lt;Func&lt;T, bool&gt;&gt;</c> unchanged.
/// One behaviour changes and the title says so: <c>Where</c> calls its delegate at every execution, so a captured variable is re-read each time, while
/// <c>WhereField</c> reads it once, at the call. It matters only for a query object kept and run again after the variable changed.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(IndexedFieldInOpaqueWhereCodeFixProvider)), Shared]
public sealed class IndexedFieldInOpaqueWhereCodeFixProvider : CodeFixProvider
{
    private const string Title = "Use WhereField (index-driven; captured values are read once, at this call)";

    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(IndexedFieldInOpaqueWhereAnalyzer.DiagnosticId);

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var diagnostic = context.Diagnostics.First();
        if (!diagnostic.Properties.ContainsKey(IndexedFieldInOpaqueWhereAnalyzer.FixableProperty))
        {
            return;
        }

        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root?.FindNode(diagnostic.Location.SourceSpan) is not SimpleNameSyntax name)
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(Title, c => RenameAsync(context.Document, name, c), equivalenceKey: Title),
            diagnostic);
    }

    private static async Task<Document> RenameAsync(Document document, SimpleNameSyntax name, CancellationToken ct)
    {
        var root = await document.GetSyntaxRootAsync(ct).ConfigureAwait(false);
        var identifier = SyntaxFactory.Identifier(name.Identifier.LeadingTrivia, "WhereField", name.Identifier.TrailingTrivia);
        return document.WithSyntaxRoot(root.ReplaceNode(name, name.WithIdentifier(identifier)));
    }
}
