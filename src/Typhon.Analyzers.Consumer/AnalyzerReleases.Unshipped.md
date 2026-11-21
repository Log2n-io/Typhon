; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md
; Move these to AnalyzerReleases.Shipped.md under a "## Release <version>" header when a package carrying them is published.

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
TYPHON001 | Performance | Error | NoCopyAnalyzer
TYPHON003 | Performance | Error | NoCopyAnalyzer
TYPHON004 | Reliability | Error | DisposableNotDisposedAnalyzer
TYPHON005 | Reliability | Error | CriticalFieldDisposalAnalyzer
TYPHON006 | Reliability | Error | CriticalFieldDisposalAnalyzer
TYPHON007 | Reliability | Error | CriticalFieldDisposalAnalyzer
TYPHON009 | Performance | Warning | SpatialMutationWithoutBarrierAnalyzer
TYPHON010 | Performance | Warning | ComponentLayoutAnalyzer
TYPHON011 | Correctness | Error | ComponentLayoutAnalyzer
TYPHON012 | Performance | Error | EntityHandleDefensiveCopyAnalyzer
TYPHON013 | Correctness | Warning | ComponentLayoutAnalyzer
TYPHON016 | Reliability | Warning | TransactionAcrossAwaitAnalyzer
TYPHON017 | Performance | Warning | IndexedFieldInOpaqueWhereAnalyzer
