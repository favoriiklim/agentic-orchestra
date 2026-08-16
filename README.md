# Agentic Orchestra

[![CI](https://github.com/favoriiklim/agentic-orchestra/actions/workflows/ci.yml/badge.svg)](https://github.com/favoriiklim/agentic-orchestra/actions/workflows/ci.yml)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](LICENSE)

**A terminal orchestrator that makes several AIs work as one team — using the chat
accounts you already pay for, instead of API keys.**

Most agent frameworks bill you per token through an API. Agentic Orchestra drives
the *web* interfaces of Gemini, ChatGPT and Claude with a real browser, and pairs
them with a local Ollama model. You log in once; after that a small local model
handles the conversation, a persistent web AI does the heavy planning, and a
short-lived squad of three web agents divides up the actual work.

Every action that touches your machine passes an approval gate first.

```
You  →  Ollama (classify)  →  Web Manager AI (plan & execute)  →  Ollama (present)  →  You
                                        │
                                        └── Squad: Innovator ∥ Implementer → Critic
```

---

## Why you might want this

- **No API keys, no per-token bill.** It uses your existing browser sessions.
- **Falls back instead of failing.** Ollama offline? The system detects it on
  every prompt and connects you straight to the web AI. Ollama comes back? It
  silently returns to the full pipeline.
- **Several models, one task.** The Innovator and Implementer run *in parallel*
  on separate browser tabs, then a Critic reviews their combined output and can
  send it back for rework.
- **It remembers.** Task telemetry accumulates, and a "dream cycle" periodically
  analyses past runs for recurring errors and injects the lessons into later sessions.
- **It asks before it acts.** See [Safety](#safety).

## Safety

The Web Manager AI runs inside a third-party web page, so **its output is untrusted
input**. Anything on that page — including text injected into it — could otherwise
arrive as a shell command on your machine. The safety layer is the boundary:

| Mode | Behaviour |
| --- | --- |
| `Ask` **(default)** | Confirms every terminal command and file write. Read-only commands like `git status` and `ls` pass through silently. |
| `Auto` | Runs without prompting. The blocklist still applies. |
| `ReadOnly` | Never executes commands or writes files. Inspection only. |

On top of the mode:

- **A blocklist that applies in every mode, including `Auto`** — `format`, `diskpart`,
  `mkfs`, `dd of=/dev/…`, `vssadmin delete shadows`, `curl … | sh`, fork bombs,
  `reg delete HKLM…`, `shutdown`, and more.
- **File writes are confined to your workspace.** Paths that resolve outside it are
  refused, including `../` escapes and sibling directories that merely share a
  prefix (`work-secrets` is not inside `work`).
- **Markdown code blocks are not executed by default.** The optional normalizer turns
  ```` ```bash ```` blocks into commands — which also means a block the AI merely
  *explained* would run. It is off unless you turn it on.
- **A refused action is reported back to the AI**, so it adapts instead of retrying blindly.

At an approval prompt you can allow once, allow for the rest of the session, skip
the action, or cancel the whole task.

> Start with `orchestra --safe` if you just want to watch what it would do.

## Requirements

- [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
- A browser login for at least one of Gemini / ChatGPT / Claude
- *(Optional)* [Ollama](https://ollama.com/) with a model pulled — `ollama pull llama3.2`.

The local model is genuinely optional. Turn it off under **Settings → Local AI**
(or leave it off when prompted at startup) and the orchestrator skips Layer 1
entirely, running **web-only** against the platforms you enabled.

Chromium is downloaded automatically on first run.

## Quickstart

```bash
git clone https://github.com/favoriiklim/agentic-orchestra.git
cd agentic-orchestra
dotnet run --project src/AgenticOrchestra/AgenticOrchestra.csproj
```

Or install it as a global tool and get the `orchestra` command:

```bash
dotnet pack src/AgenticOrchestra/AgenticOrchestra.csproj -c Release -o ./artifacts
dotnet tool install --global --add-source ./artifacts AgenticOrchestra
orchestra
```

**First run:** pick `🔑 Login to AI Platforms` from the menu. A browser window
opens; log into the platforms you want and press Enter. The session is saved to
your local profile directory, so you only do this once.

## Command line

```
orchestra [options]
```

| Option | Description |
| --- | --- |
| `-h`, `--help` | Show help and exit. |
| `-v`, `--version` | Print the version and exit. |
| `--config-path` | Print the `config.json` location and exit. |
| `--ask` | Confirm every command and file write (default). |
| `--auto` | Run actions without asking. The blocklist still applies. |
| `--safe` | Read-only: never execute commands or write files. |
| `--headed` / `--headless` | Show or hide the automated browser window. |

Safety and browser flags apply to the current run only — they are never written
back to `config.json`.

## In-chat commands

| Command | Description |
| --- | --- |
| `--help` | Show the session help and the live layer hierarchy. |
| `--clear` | Clear the conversation history. |
| `--dream` | Trigger a dream-analysis cycle now. |
| `--login` | Reopen the browser to log into platforms. |
| `--stop` | Cancel the running task. |
| `--back` / `--menu` | Return to the main menu, keeping the web AI context alive. |
| `--exit` | Tear down every layer and exit. |
| `Ctrl+C` | Cancel the task. Press again within 2s to exit. |

## How the layers work

**Layer 1 — Local model (Ollama).** Classifies each prompt. Small talk is answered
locally and never leaves your machine; real tasks are forwarded upward. It also
turns the raw telemetry from a finished task back into a readable answer.

**Layer 2 — Web Manager AI.** A persistent browser tab that plans and executes. It
acts through bracket tools the middleware understands:

| Tool | Purpose |
| --- | --- |
| `[TERMINAL_EXEC: cmd]` | Run a shell command (PowerShell on Windows, bash elsewhere). |
| `[FILE_READ: path]` | Read a file. |
| `[FILE_WRITE: path \| content]` | Write a file. |
| `[WEB_SEARCH: query]` | Search the web. |
| `[SPAWN: Name \| task]` | Delegate to a short-lived web agent in its own tab. |
| `[SPAWN_LOCAL_WORKER: Persona \| task]` | Delegate to a local Ollama sub-agent. |
| `[SPAWN_SQUAD: task]` | Deploy the full triad. |

**Layer 3 — The Squad.** The Innovator (ideas, architecture) and the Implementer
(code, commands) run simultaneously on separate tabs. The Critic reviews both and
either approves or sends the work back, up to `MaxCriticRetries` times.

**Hard fallback.** When Ollama is unreachable, Layer 1 is bypassed entirely and you
talk to the Web Manager directly. Availability is rechecked on every prompt.

## Configuration

`config.json` is created on first run and editable from the in-app **Settings** menu,
which is split into sections so you can change one thing without walking through
everything else:

`🧠 Local AI` · `🌐 Web Platforms` · `👑 Web Manager` · `👥 Squad Roles` ·
`🛡 Safety` · `💤 Dreaming` · `⏱ Timeouts` · `📝 System Prompt`

Disabling a platform automatically repoints any role that referenced it, so the
config never names a site that is switched off.

- **Windows:** `%APPDATA%\AgenticOrchestra\config.json`
- **Linux/macOS:** `~/.config/AgenticOrchestra/config.json`

Run `orchestra --config-path` to print the exact location.

<details>
<summary>Notable settings</summary>

| Section | Key | Default | Meaning |
| --- | --- | --- | --- |
| `safety` | `approvalMode` | `Ask` | `Ask`, `Auto` or `ReadOnly`. |
| `safety` | `workspaceRoot` | *(cwd)* | Directory file writes are confined to. |
| `safety` | `confineFileWritesToWorkspace` | `true` | Refuse writes outside the workspace. |
| `safety` | `normalizeCodeBlocks` | `false` | Treat markdown code blocks as executable. |
| `safety` | `blockedCommandPatterns` | *(see below)* | Regexes refused in every mode. |
| `ollama` | `enabled` | `true` | Set `false` for web-only mode — Layer 1 is skipped and never probed. |
| `ollama` | `model` | `llama3.2` | Local model for Layer 1. |
| `ollama` | `endpoint` | `http://localhost:11434` | Ollama REST endpoint. |
| `webFallback` | `managerPlatform` | `Gemini` | Which enabled platform hosts the Web Manager (Layer 2). |
| `webFallback` | `headless` | `false` | Hide the browser window. |
| `platforms[]` | `enabled` | *(Gemini only)* | Which sites may be used at all. Only enabled sites can host a role. |
| `squad` | `innovatorPlatform` / `implementerPlatform` / `criticPlatform` | `Gemini` | Which platform plays each role. Point them at different platforms to get genuinely different perspectives. |
| `squad` | `maxCriticRetries` | `3` | Rework rounds before the Critic is forced to approve. |
| `dreaming` | `telemetryThreshold` | `10` | Telemetries before a dream cycle triggers. |
| `platforms[]` | `inputSelectors` / `responseSelectors` | *(per platform)* | CSS selectors. Update these if a site's UI changes. |

</details>

### Where data lives

| What | Location |
| --- | --- |
| Config | `%APPDATA%` / `~/.config` → `AgenticOrchestra/config.json` |
| Session log & dreams | `AgenticOrchestra/` alongside the config |
| Browser profile (your logins) | `%LOCALAPPDATA%` / `~/.local/share` → `AgenticOrchestra/browser-profile` |

Nothing is sent anywhere except to the AI platforms you enable.

## Things to know before you rely on it

- **Browser automation of AI chat sites can conflict with those services' terms.**
  Check the terms for the platforms you enable and decide for yourself.
- **Web UIs change.** When a site ships a redesign, its selectors in
  `platforms[]` may need updating. That is the main maintenance cost of this approach.
- **`Auto` mode gives an AI a shell on your machine.** The blocklist and workspace
  confinement reduce the blast radius; they do not make it a sandbox. Use a VM or
  container for anything you would not want a stranger to run.
- The web layer is slower than an API — you are waiting on a real page to render.

## Development

```bash
dotnet build AgenticOrchestra.sln
dotnet test  AgenticOrchestra.sln
```

CI builds and tests on Ubuntu and Windows, treats warnings as errors, and rejects
unresolved merge-conflict markers.

Contributions are welcome — the highest-value ones are usually **selector updates**
when a platform changes its UI, and **new platform definitions** in
`AiPlatformConfig.Defaults()`.

## License

[GPL-3.0-or-later](LICENSE)
