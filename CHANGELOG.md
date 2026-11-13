# Changelog

All notable changes to FlaUI-MCP will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.3.0] - 2026-11-13

### Added
- Full computer-use surface built for the OMP harness (fork of shanselman/FlaUI-MCP):
  - **Perception:** `windows_list_windows`, `windows_snapshot` (UIA subtree cache, refs `w1e5`, `interactive_only`, `diff_since` generations), `windows_find`, `windows_get_text`, `windows_screenshot` (downscaled PNG, optional numbered marks, `image` mapping), `windows_zoom`, `windows_clipboard_read`.
  - **Actions:** `windows_click`, `windows_type`, `windows_fill`, `windows_key` (alias `windows_send_keys`), `windows_scroll`, `windows_drag`, `windows_launch`, `windows_focus`, `windows_close`, `windows_clipboard_write`, `windows_wait`, `windows_batch` (steps, wait-stable-between, snapshot-diff return).
  - **Control:** `windows_request_access` (human approves in a native topmost dialog), `windows_list_access`, `windows_release`, `windows_resume`, `windows_status`.
- Server-side safety layer: per-app `view`/`control` grants with allowlist, blocked apps (credential UI, password managers, banking titles, the harness terminal), kill switch (`Ctrl+Alt+Pause` by default: aborts tools, releases held input, revokes grants, notifies the host), user-input yield, "agent is driving" overlay excluded from captures, risky-pattern confirmation gates, password-field guard, append-only JSONL audit log.
- Optional YOLO/allow-all mode (`yolo` config, `--yolo`, `WINDOWS_MCP_YOLO`) for non-critical machines; kill switch, focus safety, stale-ref checks, audit log and overlay stay on.
- `scripts/publish.ps1` (self-contained publish, x64/ARM64) and `scripts/install-omp.ps1` (merges the `windows` entry into `~/.omp/agent/mcp.json`, installs the skill and kill-switch extension, supports `-Yolo`, `-Publish`, `-ExePath`, `-AgentDir`).
- OMP integration: operating instructions in `initialize` (appended to the system prompt), `skills/windows-computer-use/SKILL.md` with the eight operating rules, and `harness/omp/extensions/windows-computer-use.ts` which aborts the agent turn on the real kill-switch notification and queues a stop note.
- `sandbox/WindowsComputerUse.wsb` + runbook for Windows Sandbox / separate-user isolation (provided, not verified on the dev machine).
- Design notes in `DESIGN.md`; README rewritten around the new tool set, coordinate model, config reference, safety model and troubleshooting.

### Changed
- Window capture switched to `PrintWindow` (PW_RENDERFULLCONTENT) with bitblt fallback; monitor captures redact blocked-app windows.
- All coordinates inside the server are physical pixels (Per-Monitor-DPI-Aware v2); the model works in image pixels via the `image_id` mapping.
- Explorer acceptance now creates a folder, renames it, pastes its initial name through the clipboard and drags a file into it in one batch with a final snapshot diff. The drag uses a separate source window so a rename cannot silently reuse a stale destination ref.
- Safety acceptance now exercises a real held drag and main-key hold in 20-step cancellation flows with a 200 ms bound, default risky-name refusal, both drag endpoints, password refusal and explicit YOLO bypass.
- Deadlines cancel background workers; abort responses retain cooperative batch progress without waiting indefinitely on a provider. Audit entries describe the response returned to the client, and key/chord sequences are redacted.
- All held keys/buttons participate in kill-switch cleanup. Typing, chord holds and drag movement yield to user activity; JSON-loaded mixed-case allowlists work and their control grants are revoked for the session.
- Blocked-window redaction also covers screenshot/zoom BitBlt fallbacks; verified with a capture-protected target and an overlapping test window.
- The acceptance runner honors `-Milestone`, stops before running later milestones on failure, and points at the actual unit-test project.
- Same-main-model OMP acceptance covers 50 non-GUI file renames, untrusted screen-text rejection/reporting, and native hotkey notification through agent-turn abort without automatic resumption.
- Native key/text completion includes a cancellable processing grace period; key/drag holds use monotonic deadlines and tracked input releases are race-safe.
- Snapshot pruning preserves visible commands beneath empty-bounds layout ancestors, including WinUI flyouts. Notepad acceptance opens an owned document and saves through the File menu using native filename input where UIA value-setting is not committed by the common dialog.
- Audit replay formats real argument properties and retains millisecond timestamps. The optional sandbox template now maps the actual publish directory to its documented sandbox path; runtime isolation remains unverified.
- Mouse clicks, wheel scrolls and drags on a ref/mark target bring the element's window forward and return `occluded` instead of sending input to a different window covering it. Batches report `ok: false` when a step ends in `user_active` or `needs_user_confirmation`.
- `windows_list_windows` lists the taskbar and open classic popup menus that no application window owns; snapshots name such menus under `popup_menus`. `windows_click` accepts `count: 0` to hover.
- The approval dialog is laid out at the DPI of the monitor it opens on: at 300 % scaling it was 354 px wide with clipped text and two of its three buttons cut off. It now leads with the question ("Let the AI agent control …?") and centres after sizing. Derived config properties are no longer written into `config.json`.

## [0.2.0] - 2026-07-08

### Fixed
- Screenshots are now correct on scaled displays (DPI > 100%). Added `SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)` as the first call in the process entry point so UIA coordinates match physical pixels.
- Tool execution now times out after 30 seconds instead of hanging indefinitely when UI Automation blocks.

### Added
- Solution file (`FlaUI.Mcp.sln`; the `.slnx` format needs a newer SDK than the 9.0.1xx line)
- xUnit test project (`tests/FlaUI.Mcp.Tests`) with DPI regression test
- `windows_send_keys` MCP tool for sending key presses and key chords.
- Opt-in `background` mode for `windows_screenshot` handle captures, with blank-frame fallback to the normal capture path.
- `savePath` and `overwrite` options for `windows_screenshot`, with local PNG path validation and atomic writes.
- Desktop integration test project with WinForms and WPF test applications.

## [0.1.0] - 2024-02-02

### Added
- Initial release
- **Core MCP Tools:**
  - `windows_launch` - Launch Windows applications
  - `windows_snapshot` - Capture accessibility tree with element refs
  - `windows_click` - Click elements by ref (uses Invoke pattern when available)
  - `windows_type` - Type text into elements
  - `windows_fill` - Clear and fill text fields
  - `windows_get_text` - Get element text content
  - `windows_screenshot` - Capture window/element screenshots
  - `windows_list_windows` - List all open windows
  - `windows_focus` - Bring window to foreground
  - `windows_close` - Close windows
  - `windows_batch` - Execute multiple actions in a single call

- **Architecture:**
  - MCP protocol handler (JSON-RPC over stdio)
  - Element registry for ref ↔ AutomationElement mapping
  - Snapshot builder for agent-friendly accessibility tree format
  - Session manager for tracking launched applications

- **Documentation:**
  - README with installation and usage instructions
  - GitHub Actions for CI/CD
  - MIT License

### Technical Details
- Built on [FlaUI](https://github.com/FlaUI/FlaUI) for Windows UI Automation
- Uses UIA3 for modern app support (WPF, UWP, Win32)
- Targets .NET 8.0-windows
- Prefers control patterns (Invoke, Value, Toggle) over mouse simulation
