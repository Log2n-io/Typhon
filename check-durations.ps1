$src  = "test\AntHill\anthill-big.typhon-trace"
$cache = $src + "-cache-tmp"

if (Test-Path $cache) { Remove-Item $cache }

$code = @"
using System;
using System.IO;
using Typhon.Profiler;

var src   = @"$src";
var cache = @"$cache";

TraceFileCacheBuilder.Build(src, cache);
using var fs = File.OpenRead(cache);
using var reader = new TraceFileCacheReader(fs);
var s = reader.TickSummaries;
Console.WriteLine("total ticks: " + s.Count);
for (int i = 50; i < Math.Min(62, s.Count); i++)
    Console.WriteLine("tick " + s[i].TickNumber + ": " + (s[i].DurationUs / 1000f).ToString("F2") + " ms");
"@

$tmp = [System.IO.Path]::Combine($env:TEMP, "check-dur-$([guid]::NewGuid().ToString('N')).cs")
Set-Content -Path $tmp -Value $code

dotnet-csx $tmp 2>&1
if (Test-Path $cache) { Remove-Item $cache }
if (Test-Path $tmp)   { Remove-Item $tmp   }
