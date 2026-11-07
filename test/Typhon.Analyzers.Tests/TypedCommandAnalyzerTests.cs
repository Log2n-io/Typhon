using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using NUnit.Framework;

namespace Typhon.Analyzers.Tests;

/// <summary>Tests for <see cref="TypedCommandAnalyzer"/> (TYPHON015): a command sent by name while its <c>[ReplicatedMessage]</c> struct is in reach.</summary>
[TestFixture]
class TypedCommandAnalyzerTests
{
    // Stubs shaped like the real types: the analyzer binds on their metadata names.
    private const string Stubs = @"
namespace Typhon.Protocol
{
    [System.AttributeUsage(System.AttributeTargets.Struct)]
    public sealed class ReplicatedMessageAttribute : System.Attribute { }
    public sealed class RecordValues { }
}

namespace Typhon.Client
{
    public sealed class TyphonClient
    {
        public System.Threading.Tasks.Task<ushort> SendCommandAsync(string name, Typhon.Protocol.RecordValues values) => null;
        public System.Threading.Tasks.ValueTask<int> SendCommandAsync<T>(T command) where T : unmanaged => default;
    }
}

namespace Contracts
{
    [Typhon.Protocol.ReplicatedMessage] public partial struct MoveCmd { public sbyte Forward; }
    public struct NotAMessage { public int A; }
    public struct Point { public float X; public float Y; }
    [Typhon.Protocol.ReplicatedMessage] public partial struct Shaped { public Point Where; }
}
";

    private static async Task<ImmutableArray<Diagnostic>> RunAsync(string body)
    {
        var source = @"
using Typhon.Client;
using Typhon.Protocol;
using Contracts;

public static class Bot
{
    public static async System.Threading.Tasks.Task Run(TyphonClient client, RecordValues values)
    {
" + body + @"
    }
}";
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Threading.Tasks.ValueTask).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Attribute).Assembly.Location),
            MetadataReference.CreateFromFile(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")),
        };

        var compilation = CSharpCompilation.Create(
            "AnalyzerTestAssembly",
            [CSharpSyntaxTree.ParseText(Stubs), CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var compileErrors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.That(compileErrors, Is.Empty, "test source must compile: " + string.Join("; ", compileErrors.Select(e => e.GetMessage())));

        var diagnostics = await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new TypedCommandAnalyzer())).GetAnalyzerDiagnosticsAsync();
        return diagnostics.Where(d => d.Id == TypedCommandAnalyzer.DiagnosticId).ToImmutableArray();
    }

    [Test]
    public async Task ACommandSentByTheNameOfItsStructIsFlagged()
    {
        var diagnostics = await RunAsync(@"
        await client.SendCommandAsync(""MoveCmd"", values);
        await client.SendCommandAsync(nameof(MoveCmd), values);");

        Assert.That(diagnostics, Has.Length.EqualTo(2));
        Assert.That(diagnostics[0].GetMessage(), Does.Contain("Enqueue(in MoveCmd)").And.Contain("Contracts.MoveCmd"));
    }

    [Test]
    public async Task ABuiltInCommandAStructTheTypedPathCannotSendAndTheTypedPathAreNotFlagged()
    {
        var diagnostics = await RunAsync(@"
        await client.SendCommandAsync(""ClientRegion"", values);
        await client.SendCommandAsync(nameof(NotAMessage), values);
        await client.SendCommandAsync(nameof(Shaped), values);
        var name = ""Move"" + ""Cmd2"";
        await client.SendCommandAsync(name, values);
        await client.SendCommandAsync(new MoveCmd());");

        Assert.That(diagnostics, Is.Empty);
    }
}
