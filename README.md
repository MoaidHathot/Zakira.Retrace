# Zakira.Retrace

Search, read, and resume AI coding sessions across every harness you use.

Retrace reads the session stores that OpenCode, GitHub Copilot CLI, and Copilot in VS Code already
write to disk, indexes them into one local hybrid search index, and lets you find a past
conversation and reopen it — from a terminal or from an agent over MCP.

Everything is local. No account, no API key, no network at query time, no background process.

```bash
dotnet tool install -g Zakira.Retrace
retrace index build
retrace search "how did I set up the retry policy"
retrace tui                       # or browse interactively
```

Or without installing anything:

```bash
dnx Zakira.Retrace search "how did I set up the retry policy"
```

## Why

Your past sessions are the best documentation you have of your own decisions, and they are
effectively write-only. Each harness stores them in its own private format, none of them search
across each other, and two of the three have no usable search at all. "I solved this exact problem
three weeks ago" is a thought you have constantly and can act on almost never.

Retrace makes that corpus queryable, and — since it also runs as an MCP server — makes it queryable
by the agents themselves.

## Sources

| Source | Store | Search | Resume |
|---|---|---|---|
| **OpenCode** | `~/.local/share/opencode/opencode.db`, with automatic fallback to the legacy JSON tree | Retrace index | `opencode --session <id>`, `--fork` supported |
| **GitHub Copilot CLI** | `~/.copilot/session-store.db`, plus the optional JSONL event log for tool calls | Retrace index, or the harness's own FTS5 index in `--live` mode | `copilot --resume=<id>` |
| **Copilot in VS Code** | `workspaceStorage/<hash>/chatSessions/*.json` and `*.jsonl`, across stable and Insiders | Retrace index | Opens the workspace only — see below |

All reads are strictly read-only. Every harness keeps its database open while running, so Retrace
opens them with `Mode=ReadOnly` plus `PRAGMA query_only`, disables connection pooling, and falls
back to a consistent snapshot copy (database plus `-wal` and `-shm`) when the live file will not
open.

**On VS Code resume:** VS Code has no command-line switch that reopens a specific chat session.
`retrace resume` on a VS Code session prints `code <folder>`, tells you plainly that the
conversation is not restored, and points you at `retrace show` to read it. That is the honest limit
of what is possible; the alternative would be a command that looks like it worked and did not.

## Commands

```
retrace tui [query]             Browse, search, read, and resume interactively
retrace sources                 List sources and whether each is readable
retrace doctor                  Check config, sources, index, and models
retrace info                    Resolved paths, model, and search settings

retrace list                    List sessions, newest first
retrace search <query>          Search session content
retrace show <session>          Print a transcript
retrace export <session>        Export a full transcript as Markdown or JSON
retrace files <session>         List the files a session touched
retrace resume <session>        Print (or --exec) the command that reopens it
retrace tag <session>           Add, remove, or list tags

retrace index build|refresh|status|clear
retrace deps install onnx       Download the local embedding model
retrace config path|init|list|get|set
retrace mcp serve               Run as an MCP server
```

## The browser

`retrace tui` is the fast path from "I remember doing this" to sitting in that session again. It
is a full-screen, keyboard-driven browser in the lazygit mould: a search box that queries as you
type, a session list, a preview pane, and a reader — with the actions you would otherwise chain
three commands together for bound to single keys.

```
╭─ ◆ retrace ─────────────────────────────────────── hybrid · 12,039 indexed · v0.3.0 ─╮
│ ❯ retry backoff▏                                                                     │
╰──────────────────────────────────────────────────────────────────────────────────────╯
╭─ Matches · 4 ────────────────────── 160 ms ─╮╭─ Preview ─────────── opencode/ses_3aa5f96a ─╮
│▎ 3d ago   ● opencode Fix retry logic in Htt… ││ Matches  2                                   │
│▎          assistant  …add exponential backo… ││ assistant #3  …add exponential backoff to the│
│  1w ago   ● copilot  Investigate flaky integ… ││ ──────────────────────────────────────────── │
│           user  the retry test is flaky on … ││ Fix retry logic in HttpClient                │
│  2w ago   ● vscode   Harden the HTTP client… ││ retrace://opencode/ses_3aa5f96adffelfPdJUmLd │
│           assistant  …retry with jittered b… ││ dir W:\Github\Alpha · agent build · model …  │
│                                              ││                                              │
│                                              ││ ● user  10:32:01  #0                         │
│                                              ││ ▏ The retries never back off                 │
╰──────────────────────────────────────────────╯╰──────────────────────────────────────────────╯
 4 matches                               j/k move  ⏎ open  / search  r resume  c copy cmd  ? help
```

The flow the tool exists for is three keys long: type a few words, arrow to the session,
press `r`. Retrace shows the exact command and working directory, and on Enter it restores your
terminal and hands it to the harness — `opencode --session <id>` running in the directory the
session was recorded in. When the harness exits, so does Retrace.

| Key | Action |
|---|---|
| type, `/` | search as you type; empty query shows recent sessions |
| `↑`/`↓` `j`/`k`, `g`/`G`, `PgUp`/`PgDn` | move |
| `Enter`, `l` | open the reader (full transcript, `n`/`N` jump between matches, `]`/`[` between turns) |
| `Tab`, `J`/`K` | focus or scroll the preview |
| `r`, `R` | resume the session in its harness; `R` forks where supported |
| `c`, `y`, `Y`, `d` | copy the resume command / `retrace://` URI / native id / working directory |
| `o` | open the working directory in the file manager |
| `e` | export the transcript as Markdown into the current directory |
| `t` | tag the session (`-name` removes) |
| `i` | session details |
| `s`, `w`, `a`, `m` | cycle source filter, toggle current-directory filter, toggle archived, cycle search mode |
| `x`, `z` | show or hide tool output and reasoning |
| `Ctrl+R` | refresh the index (keyword-only, like the automatic top-up) and re-run the query |
| `?` | every key |

The mouse works too — wheel to scroll whichever pane it is over, click to select, click again to
open — and `--no-mouse` (or `tui.mouse: false`) gives it back to the terminal when you would
rather select text.

The browser renders in 24-bit colour wherever the terminal supports it (Windows Terminal, modern
conhost, and anything that sets `COLORTERM`), degrades to the 256-colour palette or the basic
sixteen elsewhere, and honours `NO_COLOR`. `tui.colorDepth` overrides the detection. Body text is
left at the terminal's own foreground, so the browser sits inside your theme rather than on top of
it.

Nothing slow ever runs in front of a keystroke. The embedding model is loaded once at start-up in
the background, the index top-up that the CLI performs inline runs in the background too (the
status bar shows it), and searches are queued one at a time so typing quickly never stacks work
behind itself. Previous results stay on screen, dimmed, until the current ones arrive.

The `tui` command takes the same filters as `list` and `search`, so `retrace tui --here` starts
scoped to the current directory and `retrace tui --source opencode "connection pool"` starts with a
query already running.

### Shell integration

`retrace tui --pick dir|uri|id|command` turns the browser into a picker: Enter prints the chosen
value to stdout and exits, while the interface itself draws on stderr. That is what makes it usable
inside a command substitution:

```powershell
# PowerShell: jump to the directory of a past session
function rcd { $dir = retrace tui --pick dir; if ($dir) { Set-Location $dir } }
```

```bash
# bash / zsh
rcd() { local dir; dir="$(retrace tui --pick dir)" && cd "$dir"; }
alias rr='eval "$(retrace tui --pick command)"'   # pick a session, run its resume command
```

### Filters

`list` and `search` share the same filters, so learning them once is enough:

```bash
retrace search "connection pooling" --here            # sessions in the current directory
retrace search "flaky test" --since 7d                # 7d, 12h, 30m, or 2026-01-31
retrace list --repo Orchestra --agent build
retrace list --source opencode --model claude-opus-5
retrace list --workspace P:\Github\MyProject --min-messages 10
```

### Output

Every command takes `--output json` or `--output ndjson`, so Retrace composes with other tools:

```bash
retrace list --output json | jq -r '.[] | select(.cost > 1) | .uri'
retrace search "migration" --output ndjson | head -5
```

### Identifying a session

Anywhere a session is expected you can pass a full URI, a bare native id, or an unambiguous
prefix. An ambiguous prefix is an error listing the candidates, never a guess:

```bash
retrace show retrace://opencode/ses_3aa5f96adffelfPdJUmLd91g7p
retrace show ses_3aa5f96adffelfPdJUmLd91g7p
retrace show ses_3aa5f96a
```

## Search

Search fuses two signals with reciprocal rank fusion, then applies recency and workspace boosts:

1. **Keyword** — SQLite FTS5 with BM25 ranking.
2. **Semantic** — local ONNX sentence embeddings, retrieved in two tiers: one centroid vector per
   session builds a shortlist, and only then are per-passage vectors scored. Scoring every passage
   vector on every query would mean reading the whole vector table each time.

RRF is used rather than a weighted sum because BM25 and cosine live on incomparable scales — BM25
is unbounded and corpus-dependent, cosine is bounded to [-1, 1] — so combining them directly means
an arbitrary constant decides the outcome. Fusing ranks avoids that.

```bash
retrace search "vector storage"                 # hybrid (default)
retrace search "vector storage" --lexical-only  # keyword only
retrace search "vector storage" --semantic-only # vectors only
retrace search "vector storage" --deep          # skip the shortlist, higher recall
retrace search "vector storage" --scores        # show per-signal contributions
retrace search "\"exact phrase\""               # quoted phrases are exact
```

Whatever you type is safe: every token is quoted before it reaches FTS5, so a stray `"`, `-`, `*`,
or `NEAR(` is searched for rather than interpreted as query syntax. Tokens of four characters or
more also match as prefixes (`retr` finds `retry` and `retrace`); shorter ones match whole words,
because a two-letter prefix matches most of the index and cannot rank anything meaningfully.

**Semantic search is optional.** Without a model installed, hybrid silently degrades to keyword-only
rather than failing — a missing optional dependency should not break a working feature.

### What gets indexed

| Content | Keyword | Vectors |
|---|---|---|
| User messages, assistant prose, reasoning | yes | yes |
| Tool names, titles, arguments, output; file paths and edits | yes | no |

Tool output is the overwhelming majority of stored bytes in every harness, and it embeds badly: a
vector of a build log sits in a dense, noisy region of the space and mostly returns false positives.
It stays keyword-searchable — "which session ran that command" is a real question — but never gets a
vector. That single decision is what keeps the vector store proportional to how much you actually
talked rather than to how much stdout got captured.

## Indexing

```bash
retrace index build              # first build
retrace index refresh            # incremental
retrace index build --force      # re-read everything
retrace index build --prune      # drop sessions deleted from their source
retrace index build --no-embed   # keyword-only, much faster
retrace index status
```

### Choosing what gets indexed by default

Not every harness deserves the same share of a build. A Copilot CLI that automation drives all day
can hold ten or twenty thousand sessions you will never search, and reading each one is what makes
`index build` take minutes rather than seconds. Each source has two switches:

```bash
retrace config set sources.copilot-cli.indexByDefault false   # keep it, but stop paying for it
retrace config set sources.copilot-cli.enabled false          # pretend it does not exist
```

With `indexByDefault` off, the source is skipped by `index build`, `index refresh`, and the
automatic inline top-up — and `retrace sources`, `doctor`, and the build summary all say so, so it
cannot be mistaken for a source that simply had nothing new. Everything else keeps working: it is
still listed, `show` and `resume` still read it, `--live` queries still reach it, and whatever an
explicit build put in the index is still searched. When you do want it, name it:

```bash
retrace index build --source copilot-cli --since 30d --no-embed   # just the recent part, cheaply
```

`enabled` is the blunter instrument: a disabled source is invisible everywhere and is never
indexed, even when named.

### Scoping a build by time

Embedding a large source in one pass can take hours. `--since` and `--until` narrow a build to a
window, so you can add vectors where you actually search without paying for your entire history:

```bash
retrace index build --force --since 90d --source copilot-cli
retrace index build --force --since 2026-01-01 --until 2026-06-30
```

Two things worth knowing:

- **`--since` on its own is usually a no-op.** A session already in the index is skipped — first by
  the source watermark, then by its unchanged content hash. To *add* vectors to sessions that are
  already indexed you need `--force` as well, which is why the examples above use it.
- **A scoped build never advances the watermark.** If it did, a later plain `retrace index build`
  would conclude everything older had already been handled and would never backfill the sessions
  the window excluded. Leaving the watermark alone costs one cheap re-enumeration later, which the
  content-hash check absorbs without reading a transcript or running inference.

Refresh is incremental in two independent layers: a per-source watermark narrows the candidates to
sessions whose store says they changed, and a per-session content hash then skips the ones whose
indexable content did not actually move. The second layer matters because several harnesses touch a
session row for reasons that do not alter the conversation.

**Retrace never runs in the background.** When a query finds the index stale, the top-up happens
inline, inside that command, and only after a cheap watermark comparison confirms a source moved.
Set `index.autoRefresh` to `false` to require explicit refreshes, or pass `--live` to bypass the
index entirely and read the sources directly.

Because that work sits in front of an interactive query, it is deliberately constrained so a query
can never become slow merely because indexing is outstanding:

- **It does not embed.** `index.autoRefreshEmbed` is `false` by default. Inference dominates the
  cost of indexing, and running a model over every session created today is not something a search
  should wait for. New sessions become keyword-searchable immediately and pick up vectors from
  `retrace index refresh --backfill` (see below).
- **It is time-boxed.** `index.autoRefreshMaxSeconds` (default `5`) caps the delay. Whatever
  finished is committed, and the query answers from what the index already holds. When the budget
  runs out, the shortfall is printed rather than silently paid for:

  ```
  Index is behind for opencode — run `retrace index refresh` to catch up.
  ```

Both limits exist because the alternative is worse than a stale answer: an unbounded inline build
turns a routine `retrace search` into a multi-minute hang the first time a large store drifts. If
you want a fully current index in a script, run `retrace index refresh` explicitly, or set
`index.autoRefreshMaxSeconds` to `0` to remove the ceiling.

`retrace doctor` reports the gap directly, which is easy to miss otherwise since the live and
indexed counts are separate numbers:

```
ok    index            11,916 session(s), 675,502 chunk(s), 125,109 vector(s), 1418.8 MB
warn  index freshness  not yet indexed: copilot-vscode +60 — run `retrace index refresh`
ok    vector coverage  copilot-cli 226/10,702  ·  copilot-vscode 329/329  ·  opencode 889/889
```

### Backfilling vectors

A keyword-only refresh leaves sessions in the index with no vectors, and an ordinary refresh will
not revisit them. The incremental skip is driven by the content hash, which answers *has this
conversation changed?* — not *is this session fully indexed?* Nothing changed, so nothing is
re-read, and those sessions stay absent from semantic search indefinitely.

`--backfill` asks the second question too:

```bash
retrace index refresh --backfill --source opencode      # just what is missing
retrace index refresh --backfill --since 90d            # bound it further
```

Only sessions genuinely missing vectors are re-read, so the cost tracks the size of the gap rather
than the size of the source — the distinction between this and `--force`, which re-reads everything:

```
opencode              4 indexed     886 unchanged      0 removed     1,607 chunk(s)       661 vector(s)
4 session(s) in 10.9s using bge-small-en-v1.5
```

It is opt-in rather than automatic precisely because that gap can be enormous. A source left
keyword-only on purpose represents hours of inference, and quietly folding it into every refresh
would recreate the multi-minute stall the time-boxed top-up exists to prevent. The `vector coverage`
row above is there so the state is visible when you want it, and silent when you do not; its
denominator counts sessions that *can* be embedded, so a conversation too short to be worth a vector
never shows up as a shortfall you cannot close.

For reference, on a store of ~12,500 sessions across all three harnesses, a full keyword-only build
takes about two and a half minutes and produces a ~1 GB index; subsequent refreshes with nothing
changed are instant.

## Embeddings

```bash
retrace deps install onnx                                  # default model
retrace deps install onnx --model multilingual-e5-small
retrace deps status
```

| Model | Size | Notes |
|---|---|---|
| `bge-small-en-v1.5` *(default)* | ~33 MB | English, 384-dim, CLS pooling |
| `snowflake-arctic-embed-s` | ~33 MB | English, 384-dim |
| `multilingual-e5-small` | ~118 MB | Multilingual, SentencePiece tokenizer |

Models are downloaded on first use, stored per model id so several can coexist, and verified against
the declared content length before being moved into place — a truncated download otherwise surfaces
much later as an opaque ONNX error.

Retrace refuses to query an index built with a different model than the one configured. Vectors from
different models are not comparable even when their dimensions match, so the alternative is
confident nonsense. `retrace index build --force` rebuilds after a model change.

Inference is capped at half the logical processors. ONNX Runtime otherwise claims every core, which
on a many-core machine turns an index build into something that makes the whole system unresponsive
rather than merely busy — a poor trade for work nobody is watching. Set `embeddings.maxThreads` to a
specific number to override, or leave it at `0` for the default.

## Configuration

`retrace.json` lives in your dotfiles, resolved in this order:

1. `--config <path>`
2. `RETRACE_CONFIG_PATH`
3. `$XDG_CONFIG_HOME/Zakira.Retrace/retrace.json`
4. The platform default (`%APPDATA%` on Windows, `~/Library/Application Support` on macOS,
   `~/.config` on Linux)

It is generated on first run with **every option written out at its default value**, including the
ones whose default is `null`, so the file itself documents what can be tuned.

Path values are stored verbatim and expanded at read time, so `$XDG_CONFIG_HOME/...` and `~/...`
keep working across machines that share a dotfiles repository.

```bash
retrace config path
retrace config list
retrace config set search.rrfK 80
retrace config set sources.copilot-vscode.editors stable,insiders
retrace config set sources.copilot-cli.indexByDefault false
retrace config set embeddings.enabled false
retrace config set tui.mouse false
```

The index and downloaded models are **not** stored with the config. They are large,
machine-specific, and rebuildable, so they go to `%LOCALAPPDATA%\Zakira.Retrace` (or
`$XDG_DATA_HOME`), which keeps a gigabyte of derived data out of synchronised dotfiles.

## MCP server

```bash
retrace mcp serve
```

```jsonc
{
  "servers": {
    "retrace": { "type": "stdio", "command": "retrace", "args": ["mcp", "serve"] }
  }
}
```

Or without installing: `"command": "dnx", "args": ["Zakira.Retrace", "mcp", "serve"]`.

| Tool | Purpose |
|---|---|
| `sessions-search` | Ranked search with excerpts |
| `sessions-list` | Metadata-only browse |
| `session-get` | Read a conversation, bounded and paginated |
| `session-files` | Files a session touched |
| `session-resume-command` | The command to reopen it — never executed |
| `session-tags` | Read or change tags |
| `sources-list` | Available harnesses |
| `index-status`, `index-refresh` | Index freshness |

Resources: `retrace://sources`, `retrace://sessions/recent`,
`retrace://sessions/{sourceId}/{sessionId}`.

Two rules shape the tool surface:

- **Results are token-bounded.** `session-get` returns prose only by default, caps output at 20,000
  characters, and reports a `nextTurnIndex` cursor. An agent asking about a session should not have
  its context window filled with captured stdout.
- **Nothing starts a process.** `session-resume-command` returns a string and stops. An agent asked
  a question about a session must not be able to launch an interactive harness as a side effect.

In stdio mode all logging goes to stderr, without exception, because stdout carries the JSON-RPC
framing.

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Success |
| `1` | Usage or configuration error; also `search`/`list` finding nothing |
| `2` | A source was unavailable |
| `3` | The index has not been built |
| `4` | Session not found, or an ambiguous prefix |
| `5` | The index and the runtime disagree about the embedding model |
| `70` | Unexpected error (prints a stack trace; please report it) |
| `130` | Cancelled |

## Adding a source

Sources are separate projects behind one contract, so a new harness is additive:

1. Implement `ISessionSource` in a new `Zakira.Retrace.Sources.<Name>` project.
2. Optionally implement `IIncrementalSource` for cheap refreshes, and `INativeSearchSource` if the
   harness already maintains its own full-text index.
3. Add an `Add<Name>Source()` extension and call it from the tool's composition root.

Nothing in Core changes. The same shape applies to non-harness corpora — an Obsidian vault, an
exported chat archive — as long as it can be projected onto sessions and turns.

## Layout

```
src/
  Zakira.Retrace.Abstractions/          Contracts and models. No package dependencies.
  Zakira.Retrace.Core/                  Config, storage, index, embeddings, catalog.
  Zakira.Retrace.Sources.OpenCode/
  Zakira.Retrace.Sources.CopilotCli/
  Zakira.Retrace.Sources.CopilotVsCode/
  Zakira.Retrace.Tui/                   The interactive browser: a hand-rolled terminal engine
                                        (cell buffer, diffed VT output, Win32 and termios input)
                                        with no library dependencies, plus the browser itself.
  Zakira.Retrace/                       CLI and MCP server. The packable tool.
tests/
  Zakira.Retrace.Core.UnitTests/        Config, paths, chunking, index, fusion, vectors, policy.
  Zakira.Retrace.Sources.UnitTests/     Synthetic stores built to each harness's real schema,
                                        plus live-store tests that skip when a harness is absent.
  Zakira.Retrace.Tui.UnitTests/         Input decoding, cell rendering, and the browser driven
                                        headlessly against an in-memory backend.
  Zakira.Retrace.E2ETests/              Spawns the built binary; drives MCP over real stdio.
```

Every library ships inside the single `Zakira.Retrace` tool package, so `dnx Zakira.Retrace` pulls
one artifact and gets every source.

## Building

```bash
dotnet build Zakira.Retrace.slnx
dotnet test Zakira.Retrace.slnx
./pack.ps1
```

Requires the .NET 10 SDK.

The repository's `NuGet.config` maps packages to the private proxy feeds configured on the
maintainer's machines and does not declare nuget.org. On a machine with ordinary nuget.org access
— which is what CI uses — restore with the CI configuration instead:

```bash
dotnet restore Zakira.Retrace.slnx --configfile .github/nuget.ci.config
dotnet build Zakira.Retrace.slnx --no-restore
```

## License

The Unlicense. See [LICENSE](LICENSE).