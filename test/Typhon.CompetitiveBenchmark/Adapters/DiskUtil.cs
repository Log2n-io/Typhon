using System.IO;

namespace Typhon.CompetitiveBenchmark.Adapters;

/// <summary>Sum on-disk bytes across files and/or directories (recursively) — for A5 space-amplification.</summary>
public static class DiskUtil
{
    public static long Sum(params string[] paths)
    {
        long total = 0;
        foreach (var p in paths)
        {
            if (File.Exists(p))
            {
                try { total += new FileInfo(p).Length; } catch { }
            }
            else if (Directory.Exists(p))
            {
                foreach (var f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(f).Length; } catch { }
                }
            }
        }
        return total;
    }
}
