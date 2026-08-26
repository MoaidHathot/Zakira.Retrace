---
name: zakira-retrace
description: Use when the user asks about their own past AI coding sessions — what they worked on, how they solved something before, which session touched a file, or to reopen an earlier conversation. Covers OpenCode, GitHub Copilot CLI, and Copilot in VS Code. Do not use for searching code, docs, or the web.
---

# Retrace — searching past AI coding sessions

`retrace` searches the session history of every AI coding harness on this machine and returns the
conversations behind it. Use it whenever the answer lives in something the user already did rather
than in the current codebase.

Good triggers:

- "have I dealt with this before?"
- "what was I working on last week?"
- "how did I fix the flaky test in this repo?"
- "which session touched `RetryOptions.cs`?"
- "resume that session about the migration"

Not for: searching the current repository (use file search), reading documentation, or the web.

## Start here

```bash
retrace search "connection pooling timeout"
```

Every command takes `--output json`. Prefer it when you are going to parse the result.

## Finding the right session

Search content, which is the usual entry point:

```bash
retrace search "retry backoff" --output json
retrace search "retry backoff" --here          # only the current directory
retrace search "retry backoff" --since 7d      # 7d, 12h, 30m, or 2026-01-31
retrace search "\"exact phrase\""              # quoted means exact
```

Or list by metadata when there is no obvious search term:

```bash
retrace list --here --limit 10 --output json
retrace list --repo Orchestra --since 30d --output json
```

Shared filters: `--source`, `--workspace`, `--here`, `--repo`, `--branch`, `--since`, `--until`,
`--agent`, `--model`, `--tag`, `--min-messages`.

## Reading a session

```bash
retrace show <session> --output json
retrace show <session> --turns 10-20           # a window
retrace show <session> --tools                 # include tool output
retrace show <session> --max-chars 8000        # cap the response
```

`show` returns prose only by default. Tool output is excluded because it is the bulk of a session's
size and rarely what the question is about — ask for it with `--tools` only when the answer is
plausibly inside a command's output.

A truncated response reports `nextTurnIndex`. Continue with `--turns <nextTurnIndex>-`.

A session can be named by full URI, bare id, or unambiguous prefix:

```
retrace://opencode/ses_3aa5f96adffelfPdJUmLd91g7p
ses_3aa5f96adffelfPdJUmLd91g7p
ses_3aa5f96a
```

An ambiguous prefix is an error listing the candidates. Use a longer prefix rather than picking one.

## Which files a session touched

```bash
retrace files <session> --output json
```

## Reopening a session

```bash
retrace resume <session>
```

This **prints** the command; it does not run it. Show the command to the user and let them run it.
Do not run it yourself: it starts an interactive harness and takes over the terminal.

For a Copilot-in-VS-Code session the printed command only opens the workspace — VS Code cannot
reopen a specific chat from the command line. In that case, prefer `retrace show` and summarise the
conversation directly.

## When results look wrong

```bash
retrace doctor              # config, sources, index, model
retrace index status        # size and freshness
retrace index refresh       # pick up very recent sessions
```

Retrace refreshes the index automatically when it detects a source has changed, so an explicit
refresh is usually unnecessary. Reach for it when the user just finished a session and expects to
find it immediately.

That automatic refresh is time-boxed and keyword-only, so a very recent session is findable by
keyword right away but may not yet be reachable by meaning. If a semantic search misses something
the user is sure exists, try distinctive keywords before concluding it is absent. The `vector
coverage` row in `retrace doctor` shows which sources are fully embedded.

Adding the missing vectors is `retrace index refresh --backfill --source <id>`. Do not run it
unprompted: on a source deliberately kept keyword-only it can take hours. Suggest it and let the
user decide.

`retrace search` exits non-zero when there are no matches; that is a normal outcome, not a failure.

## Interpreting output

- `uri` is the stable handle. Use it, not the bare id, when passing a session between commands.
- `source` is which harness recorded it: `opencode`, `copilot-cli`, or `copilot-vscode`.
- `score` is only comparable within a single result set.
- `snippets` are the matched passages, already trimmed for display.
- `cost` is in the harness's own accounting units and is not comparable across sources.

## Practical notes

- Two or three specific terms beat one generic one. Hybrid search fuses keyword and semantic
  signals, so exact identifiers and paraphrases both work.
- Add `--here` when the user says "this project"; it is almost always what they mean.
- Search first, then `show` the one or two most promising results. Do not fetch transcripts for a
  whole result page.
- Tool output is keyword-searchable even though it is never embedded, so searching for a literal
  command or error string does work.
