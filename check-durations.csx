#r "src/Typhon.Profiler/bin/Debug/net10.0/Typhon.Profiler.dll"
using Typhon.Profiler;

var trace = @"test\AntHill\anthill-big.typhon-trace";
var cache = trace + "-cache-test";
if (File.Exists(cache)) File.Delete(cache);
Console.WriteLine("Building cache...");
TraceFileCacheBuilder.Build(trace, cache);
using var fs = File.OpenRead(cache);
using var reader = new TraceFileCacheReader(fs);
var summaries = reader.TickSummaries;
Console.WriteLine($"Total ticks: {summaries.Count}");
for (int i = 50; i < Math.Min(60, summaries.Count); i++) {
    var s = summaries[i];
    Console.WriteLine($"tick {s.TickNumber}: {s.DurationUs/1000f:F2}ms");
}
File.Delete(cache);
