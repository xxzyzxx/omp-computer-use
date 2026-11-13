# Windows Computer-Use MCP

An MCP (Model Context Protocol) server that lets an AI agent operate native Windows
desktop applications — through UI Automation refs, not pixel guessing — behind a
server-side safety layer designed for an agent that runs mostly unattended.

Fork of [shanselman/FlaUI-MCP](https://github.com/shanselman/FlaUI-MCP), which applied
the Playwright snapshot/ref pattern to Windows; this fork turns it into a full
computer-use surface (refs + set-of-marks screenshots + batch + safety gates) built
for the [OMP](https://github.com/can1357/oh-my-pi) harness.

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

## How it works

```
Agent (main model) ── stdio MCP ──► FlaUI.Mcp.exe (.NET 8, FlaUI 5)
  snapshot → click ref → verify        │ UIA3 / UIA2 trees, refs w1e5
                                       │ PNG screenshots + numbered marks
                                       │ SafetyGate: grants, kill switch, overlay,
                                       │   user-input yield, confirm dialogs, audit
                                       ▼
                                    Windows desktop (physical pixels inside)
```

There is **no second model**: the server does no perception of its own. The main model
reads the UIA tree and the screenshots and decides everything. Coordinates you send
are image pixels; the server maps them to physical pixels.

## Install (OMP)

Requires Windows 10/11 and the [.NET 8 SDK](https://dotnet.microsoft.com/download) to
publish (the output itself is self-contained).

```powershell
# from the repo root; publishes self-contained to publish/win-x64
./scripts/install-omp.ps1 -Publish

# with YOLO / allow-all mode (see "Safety model" first)
./scripts/install-omp.ps1 -Publish -Yolo
```

The script merges the `windows` entry into `~/.omp/agent/mcp.json` (preserving other
servers), copies the skill to `~/.omp/agent/skills/windows-computer-use/SKILL.md` and
the kill-switch extension to `~/.omp/agent/extensions/windows-computer-use.ts`. Then
in omp: `/mcp reload` (or restart), `/mcp list` to confirm. Tools register as
`mcp__windows_snapshot`, `mcp__windows_click`, … and as `xd://mcp__windows_*` devices.

### Manual mcp.json

See [`harness/omp/mcp.example.json`](harness/omp/mcp.example.json):

```json
{
  "$schema": "https://raw.githubusercontent.com/can1357/oh-my-pi/main/packages/coding-agent/src/config/mcp-schema.json",
  "mcpServers": {
    "windows": {
      "type": "stdio",
      "command": "C:\\path\\to\\publish\\win-x64\\FlaUI.Mcp.exe",
      "args": [],
      "timeout": 200000
    }
  }
}
```

### Publishing by hand

```powershell
./scripts/publish.ps1            # win-x64
./scripts/publish.ps1 -Arm64     # win-arm64
```

## Tool reference

All tools are namespaced `windows_*` in the `windows` server. `window` specs accepted
by most tools: omitted = foreground, `w3` = window handle, `pid:1234`, a title
substring, or a process name. Targeting order everywhere: `ref` → `mark` → `x,y`
(+ optional `image_id`).

| Tool | Purpose | Key params |
|---|---|---|
| `windows_list_windows` | Top-level windows: handle, pid, process, title, monitor, rect. Ungated. | — |
| `windows_snapshot` | Pruned UIA tree with refs `w1e5`, roles, names, bounds, a generation number for diffing. | `window`, `interactive_only`, `diff_since` |
| `windows_find` | Search element names in a window; returns refs. | `window`, name filter |
| `windows_get_text` | Text content of an element, in an `<untrusted_screen_text>` block. | `ref` |
| `windows_screenshot` | PNG of a window/monitor (or `monitor: "all"`); `marks: true` overlays numbered marks for interactive elements. Returns `image` mapping with `image_id`. | `window`, `monitor`, `marks` |
| `windows_zoom` | Magnified crop around a point/region of the latest image; for small or dense areas. | `x`, `y`, `image_id`, `window` |
| `windows_click` | Click via Invoke/Toggle/Selection patterns, else synthetic click at the clickable point. | `ref` \| `mark` \| `x,y`(+`image_id`) |
| `windows_type` | Type text into an element (Value pattern / focus + keys). | `ref`, `text` |
| `windows_fill` | Clear and fill a field. | `ref`, `value` |
| `windows_key` (alias `windows_send_keys`) | Key presses/chords to the focused element, e.g. `["Ctrl+S", "Enter"]`. | `chord`, `keys` |
| `windows_scroll` | Scroll at a target. | `ref` \| `x,y`, direction/amount |
| `windows_drag` | Drag from one target to another. | from/to `ref` or `x,y` |
| `windows_launch` | Start an app; gated like any action. | `app`, `args` |
| `windows_focus` | Bring a window to the foreground. | `window` |
| `windows_close` | Close a window. | `window` / `ref` |
| `windows_clipboard_read` | Read clipboard text. | — |
| `windows_clipboard_write` | Write clipboard text. | `text` |
| `windows_wait` | Wait for UI quiet / element state before verifying. | `window`, `stable`, `timeout_ms` |
| `windows_batch` | Run 2+ steps in one call, optionally wait-stable between steps, return a final snapshot diff or screenshot. | `steps`, `wait_stable_between`, `return: "snapshot_diff"\|"screenshot"` |
| `windows_request_access` | Ask the human for `view`/`control` on exe names, with a one-line reason; a topmost dialog asks. | `apps`, `tier`, `reason` |
| `windows_list_access` | Current grants and control lock state. | — |
| `windows_release` | Drop grants and the control lock when done. | — |
| `windows_resume` | Clear the user-active pause once the user says go ahead. | — |
| `windows_status` | Server/safety status: mode, hotkey, grants, audit dir. | — |

## Coordinate model

Inside the server every coordinate is a **physical pixel** (Per-Monitor-DPI-Aware v2;
tested at 300 % scaling). The model never sees physical pixels: every image result
carries an `image` mapping (`image_id`, size, origin, scale), and any `x,y` you pass
is interpreted as pixels of the most recent image — or of `image_id` if given — and
mapped back for you. Points outside the frame return `bad_coordinates` with "take a
new screenshot" as the remedy. On multi-monitor setups (e.g. monitor 2 at negative
y), windows carry their monitor id; screenshots never span monitors unless you ask
for `monitor: "all"`.

## Configuration

`config.json` lives at `%LOCALAPPDATA%\WindowsComputerUse\config.json`
(override: `--config <path>` or `WINDOWS_MCP_CONFIG`). A file with defaults is
written on first run; the full annotated example is
[`src/FlaUI.Mcp/config.example.json`](src/FlaUI.Mcp/config.example.json).
CLI flags and environment variables override the file.

| Key | Default | What it does | Override |
|---|---|---|---|
| `yolo` | `false` | Allow-all mode (see safety model). | `--yolo` / `WINDOWS_MCP_YOLO` |
| `toolTimeoutMs` | `30000` | Per-call tool timeout. | — |
| `uiaTransactionTimeoutMs` | `5000` | Per-UIA-call provider timeout. | — |
| `uiaConnectionTimeoutMs` | `3000` | UIA connection timeout. | — |
| `uia2Apps` | `[]` | Exe names automated via UIA2 instead of UIA3. | — |
| `maxScreenshotEdge` | `1568` | Screenshot long-edge downscale target. | `--max-edge N` |
| `snapshotMaxChars` | `24000` | Snapshot output cap (~4 chars/token). | — |
| `snapshotMaxNodes` | `600` | Snapshot node cap after pruning. | — |
| `killSwitchHotkey` | `ctrl+alt+pause` | Kill-switch hotkey (RegisterHotKey syntax). | `--hotkey` |
| `idleResumeSeconds` | `3.0` | Idle seconds after user input before actions may resume. | — |
| `userInputMouseDeadZonePx` | `4` | Mouse jitter below this many px is ignored. | — |
| `userActiveWaitMs` | `8000` | How long an action waits for idle before returning `user_active`. | — |
| `overlay` | `true` | "Agent is driving" topmost overlay while control is held. | `--no-overlay` / `WINDOWS_MCP_NO_OVERLAY` |
| `prompts` | `"dialog"` | Approval surface: `"dialog"` (topmost window) or `"deny"` (headless: all requests refused). | `--prompts dialog\|deny` / `WINDOWS_MCP_PROMPTS` |
| `promptTimeoutSeconds` | `60` | Human answer window for dialogs. | — |
| `allowlist` | `{}` | Apps holding a tier without asking: `{"notepad.exe": "control"}`. | — |
| `blockedApps` | see example | Refused even with a grant; `.exe` entries match the process, others are title regexes. | `allowBlockedApps` / YOLO |
| `allowBlockedApps` | `false` | Override the blocklist (implied by YOLO). | — |
| `blockHarnessTerminal` | `true` | Also block the process chain that launched the server (your terminal). | — |
| `riskyPatterns` | see example | Element-name regexes that require human confirmation before a click. | — |
| `passwordGuard` | `true` | Refuse to type into UIA password fields. Off under YOLO. | — |
| `auditDir` | `%LOCALAPPDATA%\WindowsComputerUse\audit` | Append-only JSONL audit log; empty disables. | `--audit-dir` / `--no-audit` |
| `grantsPath` | `%LOCALAPPDATA%\WindowsComputerUse\grants.json` | Persisted "always" grants. | — |
| `electronApps` | see example | Electron/Chromium hosts that get the thin-tree hint. | — |

## Safety model

Everything here is enforced **server-side**, in the process the agent cannot touch:

- **Access grants.** Action tools refuse un-granted apps; `windows_request_access`
  shows a topmost dialog with your reason; a human approves. Grants are per-app and
  per-tier; `windows_release` drops them.
- **Kill switch.** `Ctrl+Alt+Pause` (configurable): aborts running tools and batches,
  releases held mouse/keyboard input, revokes control grants, and notifies the host
  (`notifications/windows/kill_switch`). The bundled OMP extension turns that into
  `ctx.abort()` — the current agent turn stops — plus a user warning and a next-turn
  note telling the model to stop. In omp you can also run `/windows-killswitch`.
- **User input wins.** Real mouse/keyboard activity pauses injection until the user
  has been idle `idleResumeSeconds`; the agent gets `user_active` and must wait.
- **Overlay.** A topmost "agent is driving" frame while the control lock is held; it
  is excluded from captures.
- **Untrusted screen text.** All screen-derived text is wrapped in
  `<untrusted_screen_text>` blocks; the skill and the server instructions tell the
  model to treat it as data and report embedded instructions.
- **Confirmation gates.** Clicks on elements matching `riskyPatterns` (pay, delete
  permanently, uninstall, …) return `needs_user_confirmation` and only proceed after
  a human confirms the dialog.
- **Password guard.** UIA password fields are never typed into.
- **Audit log.** Append-only JSONL of every tool call (arguments redacted for
  secrets) in `auditDir`; replay calls with `node scripts/mcp-call.mjs <exe> -- '<tool>' '<json>'`.
- **Blocked apps.** `consent.exe`, UAC/credential UI, password managers, Windows
  Security, banking/payment title regexes — and, by default, the terminal that
  launched the server (so the agent cannot automate its own harness).

**What YOLO turns off:** access prompts (every app auto-granted `control`, blocked
apps included), confirmation gates, the user-input pause, and the password-field
guard. **What stays on:** the kill switch, focus safety, stale-ref checks, the audit
log, and the overlay (disable the overlay separately with `--no-overlay`).

## Platform notes

- **DPI.** Per-Monitor-DPI-Aware v2 via `app.manifest`; developed and verified on two
  3840×2160 monitors at 300 % scaling. Never assumes 96 DPI.
- **Electron.** Chromium trees are thin; the server hints `marks` +
  `--force-renderer-accessibility` for known Electron apps.
- **UIA2.** Some WinForms apps behave better with the legacy provider; list their exe
  names in `uia2Apps`.
- **Multi-monitor.** Windows report their monitor; captures are per-monitor unless
  `monitor: "all"`. Works with mixed DPI and negative-origin monitors.

## Known limits

- No `Windows.Graphics.Capture` path; window capture uses `PrintWindow`
  (PW_RENDERFULLCONTENT) with a bitblt fallback. Some DirectX-exclusive apps come
  back blank.
- No guarantees over remote-session software (Parsec, RDP with GPU remoting, …);
  untested there.
- Monitor screenshots **redact blocked-app windows** rather than showing them.
- `windows_launch` of an app you have not been granted will refuse, like any action.
- Windows Sandbox / separate-user isolation: scripts are provided under
  [`sandbox/`](sandbox/README.md) but are **not verified** on the development machine.
- Integration tests need an interactive desktop session; CI runs unit tests only.

## Troubleshooting

| Symptom | Fix |
|---|---|
| `/mcp list` shows the server failing | Run the exe from a terminal; check the stderr banner (`yolo=`, `hotkey=`) and any `[config]` notes. |
| `stale_ref` repeatedly | The UI is changing under you; take a fresh `windows_snapshot` and use the new refs. |
| `bad_coordinates` | Your `x,y` predates the latest image; take a new screenshot (or pass the right `image_id`). |
| `user_active` loops | The human is typing/moving the mouse; wait, or ask them to stop touching the machine. |
| `needs_user_confirmation` never resolves | A topmost dialog is waiting on the desktop; tell the user to answer it. |
| Kill switch does not register | The hotkey is taken (`registered=false` in the stderr banner and audit log); pick another with `--hotkey`. |
| Blank screenshots | The app renders via DirectX/hardware overlay; try `windows_snapshot` instead, or focus the window first. |
| Server blocked from automating your terminal | That is `blockHarnessTerminal`; launch omp from a different terminal than the one you want automated, or set it to `false` deliberately. |

## Building from source

```powershell
dotnet build FlaUI.Mcp.slnx
dotnet test tests/FlaUI.Mcp.Tests          # unit tests
dotnet test tests/FlaUI.Mcp.IntegrationTests  # needs an interactive desktop session
```

## License & acknowledgments

MIT — see [LICENSE](LICENSE). Built on [FlaUI](https://github.com/FlaUI/FlaUI)
(.NET UI Automation), inspired by [Playwright](https://playwright.dev/)'s
snapshot/ref interaction model and the
[Model Context Protocol](https://modelcontextprotocol.io/). Upstream project:
[shanselman/FlaUI-MCP](https://github.com/shanselman/FlaUI-MCP).
