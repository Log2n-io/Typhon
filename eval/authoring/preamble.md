# Context

You are a developer building an application on the **Typhon** database engine, consumed as the NuGet package `Typhon`.
`Solution.csproj` in this folder already references it (from a local feed; do not change the package reference or `nuget.config`).

What you have:
- `docs/llms.txt` — the documentation index for coding agents, and `docs/llms-full.txt` — the full documentation corpus.
- The package itself (its XML documentation is available to the compiler and in the NuGet cache).

Rules:
- Work **only inside this folder**. Do not read any other directory on this machine: you are an external developer and do not have
  the engine's source code.
- Implement the contract below in `Solution.csproj` (add any files you need). The class and member signatures of the contract are
  checked by an automated harness: keep them exactly as written (`public static class Solution` in the global namespace unless the
  task says otherwise).
- Files whose first line says `DO NOT MODIFY` must stay as they are.
- `dotnet build` must succeed. You may create a separate console project in a subfolder to try your code; the harness only uses
  `Solution.csproj`.
- Your code will be exercised by an automated checker in a fresh process, with fresh database paths. Write it as production code.

When you are done, reply with a short summary of your design choices.

# Task
