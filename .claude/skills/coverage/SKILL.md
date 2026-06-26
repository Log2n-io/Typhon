---
name: coverage
description: Run code coverage analysis, track class-level results, and generate trend reports
argument-hint: [--report-only]
---

# Code Coverage Tracking

Run dotnet-coverage against Typhon.Engine.Tests, record class-level coverage history in JSONL, and generate trend reports with regression detection.

## Input

$ARGUMENTS may contain:
- `--report-only` — Skip coverage execution, regenerate reports from existing history
- (empty) — Full coverage run + report generation

## Workflow

### `/coverage --report-only`

Skip coverage execution. Regenerate reports from existing history:

```bash
python3 coverage/scripts/report_generator.py --history coverage/history/results.jsonl --config coverage/config.json --output-dir coverage/reports --source-dir src/Typhon.Engine
```

Read `coverage/reports/latest.md` and display a condensed summary.

### `/coverage` (default — full run + report)

#### Step 1: Verify dotnet-coverage

Check that dotnet-coverage is available:

```bash
dotnet-coverage --version
```

If not installed, tell the user to install it:
```
dotnet tool install --global dotnet-coverage
```

#### Step 2: Build Debug

```bash
dotnet build -c Debug test/Typhon.Engine.Tests/Typhon.Engine.Tests.csproj
```

If build fails, report errors and stop.

#### Step 3: Run dotnet-coverage

```bash
dotnet-coverage collect -o coverage/coverage-report.xml -f xml dotnet test "test/Typhon.Engine.Tests/Typhon.Engine.Tests.csproj" --no-build -c Debug
```

**IMPORTANT:** This step can take a minute or two. Let the user know coverage analysis is running.

#### Step 4: Generate Report

```bash
python3 coverage/scripts/report_generator.py --coverage-xml coverage/coverage-report.xml --history coverage/history/results.jsonl --config coverage/config.json --output-dir coverage/reports --source-dir src/Typhon.Engine
```

#### Step 5: Clean Up

Remove the temporary coverage XML report:

```bash
rm -f coverage/coverage-report.xml
```

#### Step 6: Display Summary

Read `coverage/reports/latest.md` and display a condensed summary to the user:

- Overall coverage percentage and statement counts
- **Regressions found** (list each class with % drop) — highlight prominently
- **Improvements found** (list each class with % gain)
- Area coverage breakdown
- Link to full report: `coverage/reports/latest.md`

#### Step 7: Prompt for History Commit

Ask the user:

**Question:** "Coverage results have been appended to history. Commit the updated report?"
**Header:** "Commit"
**Options:**
- `Yes, commit report` (description: "Commit the history + rendered report (latest.md + charts)")
- `No, skip commit` (description: "Keep the local changes without committing")

The rendered report (`coverage/reports/latest.md` + `coverage/reports/charts/`) is now **tracked** —
`latest.md` embeds the charts, so they must be committed together to avoid broken images. Coverage is
hardware-independent, so committing from a local run is fine. If yes, commit the full set:
```bash
git add coverage/history/results.jsonl coverage/reports/latest.md coverage/reports/charts
git commit -m "coverage: record code coverage results"
```
