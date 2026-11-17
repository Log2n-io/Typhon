using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NUnit.Framework;
using Typhon.Engine;

namespace EvalChecker;

internal static class H
{
    /// <summary>The agent's workspace (the folder holding Solution.csproj).</summary>
    public static string SolutionDir => Environment.GetEnvironmentVariable("EVAL_SOLUTION_DIR")
        ?? throw new InvalidOperationException("EVAL_SOLUTION_DIR not set");

    /// <summary>A fresh database file path in its own temp directory.</summary>
    public static string NewDbPath(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "typhon-eval", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name + ".typhon");
    }

    /// <summary>Every C# file of the agent's project (bin/obj and subfolder projects excluded).</summary>
    public static List<SyntaxTree> Sources()
    {
        var trees = new List<SyntaxTree>();
        foreach (var file in Directory.EnumerateFiles(SolutionDir, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(SolutionDir, file).Replace('\\', '/');
            if (rel.StartsWith("bin/") || rel.StartsWith("obj/") || rel.StartsWith("_checker/") || rel.Contains("/bin/") || rel.Contains("/obj/"))
            {
                continue;
            }
            // Subfolders holding their own project are the agent's scratch programs, not the solution.
            var dir = Path.GetDirectoryName(file);
            bool inOtherProject = false;
            while (dir != null && dir.Length > SolutionDir.TrimEnd('/', '\\').Length)
            {
                if (Directory.EnumerateFiles(dir, "*.csproj").Any())
                {
                    inOtherProject = true;
                    break;
                }
                dir = Path.GetDirectoryName(dir);
            }
            if (inOtherProject)
            {
                continue;
            }
            trees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: rel));
        }
        return trees;
    }

    public static IEnumerable<T> All<T>(this List<SyntaxTree> trees) where T : SyntaxNode
        => trees.SelectMany(t => t.GetRoot().DescendantNodes().OfType<T>());

    /// <summary>Runs the runtime until it has completed at least <paramref name="ticks"/> ticks, then shuts it down.</summary>
    public static long RunTicks(TyphonRuntime rt, long ticks, int timeoutMs = 30_000)
    {
        rt.Start();
        var sw = Stopwatch.StartNew();
        while (rt.CurrentTickNumber < ticks)
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                rt.Shutdown();
                Assert.Fail($"runtime reached only tick {rt.CurrentTickNumber} of {ticks} in {timeoutMs} ms");
            }
            Thread.Sleep(5);
        }
        rt.Shutdown();
        return rt.CurrentTickNumber;
    }

    /// <summary>Waits for a condition while the runtime ticks.</summary>
    public static bool WaitFor(Func<bool> condition, int timeoutMs = 15_000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                return false;
            }
            Thread.Sleep(5);
        }
        return true;
    }

    /// <summary>Union of every read declaration plus writes.</summary>
    public static HashSet<Type> ReadSet(SystemAccessDescriptor a)
    {
        var s = new HashSet<Type>(a.Reads);
        s.UnionWith(a.ReadsFresh);
        s.UnionWith(a.ReadsSnapshot);
        s.UnionWith(a.AdditionalReads);
        s.UnionWith(a.Writes);
        s.UnionWith(a.SideWrites);
        return s;
    }

    /// <summary>True when <paramref name="to"/> is reachable from <paramref name="from"/> through DAG successor edges.</summary>
    public static bool Reaches(SystemDefinition[] all, SystemDefinition from, SystemDefinition to)
    {
        var seen = new HashSet<int>();
        var stack = new Stack<int>();
        stack.Push(from.Index);
        while (stack.Count > 0)
        {
            int i = stack.Pop();
            if (!seen.Add(i))
            {
                continue;
            }
            var def = all.FirstOrDefault(d => d.Index == i);
            if (def == null)
            {
                continue;
            }
            foreach (var s in def.Successors)
            {
                if (s == to.Index)
                {
                    return true;
                }
                stack.Push(s);
            }
        }
        return false;
    }

    /// <summary>True when <paramref name="after"/> is guaranteed to observe this tick's writes of <paramref name="before"/>.</summary>
    public static bool OrderedAfter(SystemDefinition[] all, SystemDefinition before, SystemDefinition after)
        => ReferenceEquals(before, after)
           || after.PhaseIndex > before.PhaseIndex
           || Reaches(all, before, after);
}
