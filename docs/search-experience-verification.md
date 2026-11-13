# Provider controls and TUI search: verification

Verified on 2026-11-13 with .NET 10 on Windows. The user-facing controls and examples are in
[the README](../README.md#the-browser).

## Completed checks

- Release build and full suite: **267 passed, 0 failed**. The two opt-in performance tests were
  skipped in the normal suite and each passed separately with measurement output enabled.
- Headless snapshots captured and reviewed at all four sizes listed below, including rich/literal
  preview and compact provider controls.
- NuGet tool and symbol packages built successfully. The extracted tool package executed provider
  disablement and an excluded-provider refresh using an isolated config and index directory.
- `git diff --check` passed; maintained text files have LF line endings and no `nul` file exists.

## Acceptance coverage

| Requirement | Controls | Evidence |
|---|---|---|
| Disable providers during indexing | `sources disable <provider> --index-only`, `sources enable`, `index build/refresh --exclude-provider` | `IndexPolicyTests` checks source reads, default top-ups, explicit overrides, exclusions, and disable/re-enable against an existing index. CLI subprocess tests check persistence and both build commands. |
| Search a specific provider | `provider:opencode`, `source:`, comma-separated/repeated values, exclusions | `BrowserQueryTests`, `BrowserFilterTests`, and the index regression with a one-candidate cap check parsing, propagation, and filtering before retrieval limits. |
| Toggle multiple providers | `F2` / `p`, Space, all/none, mouse | Headless interaction tests cover multiple selections, none, initial CLI scopes, synchronization with query facets, and all providers disabled. |
| Time filters, order and groups | `F3` / `f`; `since:`, `until:`, `last:`; `sort:`, `group:`; `O`, `b`, `0` | Tests cover week/month presets, inclusive dates, invalid ranges, browse ordering before limits, selectable sessions beneath group headers, immediate presentation changes, reset, and narrow-screen controls. |
| Rich previews | Markdown headings/emphasis/links/lists/quotes/tables, code syntax accents, tool output and diffs; `v`, `x`, `z` | Formatter tests inspect content, styles, wrapping, indentation, malformed markup, Unicode and terminal controls. Source fixture tests check paging across Copilot CLI and both VS Code store formats. |
| Faster, responsive searches | Native SQLite cancellation, debounce, latest-request handling, cached formatting, lightweight header reads | Deterministic concurrency tests cover cancellation, no overlapping searches, late results/errors, stale picker selections, preview races, and refreshed transcripts. Separate synthetic benchmarks record retrieval, excerpts, cancellation, and frame costs. |

Additional regressions fixed while implementing these controls:

- Disabled providers leaking from an existing index, including identifier resolution.
- Source-qualified prefixes resolving to another provider.
- Missing snippets on semantic-only hits and duplicate passages after fusion.
- Session rank being penalized by repeated passages in preceding sessions.
- Copilot page indices restarting at zero and total counts using exchanges instead of messages.
- A live-store test incorrectly requiring an assistant reply in a user-only session.
- Failed transcript reads advancing the index watermark past data that still needed indexing.
- Backlog notices and preview/header state surviving a successful refresh.
- Stored tags disappearing from returned session metadata.
- Long wide-character words overflowing their wrap width, and control characters entering terminal output.

## Performance evidence

The search benchmark creates **10,000 synthetic sessions and 200,000 passages**. It uses keyword
search, 50 results and three excerpts per result. Each metric has two warm-up runs followed by
seven timed samples. Assertions run outside the timed regions. Measurements below were collected
on Windows build 26200, .NET runtime 10.0.15, with 32 logical processors.

| Measurement | Median |
|---|---:|
| Original BM25 candidate SQL | 45.60 ms |
| Hidden-rank candidate SQL with metadata filtering | 35.05 ms |
| 150 excerpt lookups using repeated row-id equality scans | 198.42 ms |
| Equivalent excerpt lookups using a bounded scan and membership check | 1.72 ms |
| Complete keyword search, including excerpts | **40.74 ms** |
| Complete provider + last-week search, including excerpts | **50.24 ms** |
| Header metadata read | 1.54 ms |
| Full index/vector coverage status read | 50.26 ms |

Before the excerpt optimization, the complete keyword query on this same fixture measured
241.15 ms. The final 40.74 ms measurement is about **83% lower**. The query plans explain the
difference: FTS5 used `=M1` for `rowid IN (...)`, reopening the full-text cursor for each id;
the bounded membership query uses `M1><`, with one full-text cursor. The benchmark also measures
an FTS-first join alternative and checks that all variants return the expected excerpt count.

A long query with cancellation scheduled at 20 ms completed cancellation in **22.49 ms** total.
A separate test cancels from inside an executing SQLite statement and asserts `SQLITE_INTERRUPT`,
so this verifies cancellation during database work rather than only before the next await.

The rendering benchmark uses **400 turns / 68,000 characters** of Markdown at 140×40. Initial rich
formatting and drawing took 36.70 ms; 100 cached frames had a **0.15 ms median** and 2.98 ms maximum.
These are reproducible observations, not timing thresholds imposed on CI. ONNX inference is not
part of the keyword benchmark; vector retrieval correctness uses deterministic embedding fixtures.

## Reproduce

Run the normal release build and suite:

```powershell
dotnet restore Zakira.Retrace.slnx
dotnet build Zakira.Retrace.slnx --configuration Release --no-restore
dotnet test Zakira.Retrace.slnx --configuration Release --no-build
```

The opt-in benchmarks write `search.json` (including query plans) and `rendering.json`. Run them
sequentially so the two workloads do not compete for CPU:

```powershell
$env:RETRACE_BENCHMARK_DIRECTORY = Join-Path $env:TEMP 'retrace-evidence'
dotnet test tests/Zakira.Retrace.Core.UnitTests --configuration Release --filter FullyQualifiedName~SearchPerformanceTests
dotnet test tests/Zakira.Retrace.Tui.UnitTests --configuration Release --filter FullyQualifiedName~RenderingPerformanceTests
$env:RETRACE_BENCHMARK_DIRECTORY = $null
```

Capture the actual cell-buffer output for browse, filtered/grouped search, provider controls, rich
and literal readers, help, and 132×34 / 80×24 / 60×18 / 40×12 layouts:

```powershell
$env:RETRACE_TUI_SNAPSHOT = Join-Path $env:TEMP 'retrace-tui-frames.txt'
dotnet test tests/Zakira.Retrace.Tui.UnitTests --configuration Release --filter FullyQualifiedName~SnapshotTests
$env:RETRACE_TUI_SNAPSHOT = $null
```

The interaction and layout checks drive the browser headlessly through its real input handlers
and renderer. Colour/style assertions are separate from the plain-text snapshots.

## Implementation references

- [Microsoft.Data.Sqlite async limitations](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async):
  its async methods execute synchronously; cancelling a task alone does not interrupt an executing query.
- [SQLite progress callbacks](https://www.sqlite.org/c3ref/progress_handler.html): returning nonzero
  interrupts SQLite work on the query thread.
- [FTS5 ranking](https://www.sqlite.org/fts5.html#sorting_by_auxiliary_function_results): ordering by the
  hidden rank column permits optimizations that `ORDER BY bm25(...)` cannot use.
- [SQLite unary-plus query planning](https://www.sqlite.org/optoverview.html#disqualifying_where_clause_terms_using_unary_):
  the excerpt query disqualifies only the row-id membership constraint; row ids and parameters are
  integers, preserving comparison semantics. The benchmark records the plans motivating this choice.
