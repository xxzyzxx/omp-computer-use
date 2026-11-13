# Design notes — Windows computer-use MCP (0.3.0)

Decisions and deviations from the obvious implementation, with reasons. The tool
surface and config keys are documented in the README; this file explains the *why*.

## Capture: PrintWindow, not Windows.Graphics.Capture

Window capture first uses `PrintWindow` with `PW_RENDERFULLCONTENT`, which can
capture an occluded window without focusing it. A failed or nearly black result
falls back to a BitBlt of that window's visible screen rectangle, reported as
`bitblt-fallback`. The fallback can include other windows covering the target;
blocked windows are therefore redacted in both screenshot and zoom paths.
Windows.Graphics.Capture is not implemented: the current native path covers the
Win32, WinForms, WPF and Chromium applications exercised here. DirectX-exclusive
and capture-protected surfaces remain limits rather than promised support.

## Snapshot: one UIA Subtree cache fetch

Snapshot walks the tree with a `CacheRequest` scoped to the subtree (elements,
properties, patterns) rather than issuing a UIA property round-trip for every field
of every node. The cache is a point-in-time view, which is exactly what a snapshot
should be; this does not establish a measured speedup against an uncached baseline.

## Refs live across generations, keyed by RuntimeId

Refs (`w1e5`) are minted per window (`w1` = window handle, `e5` = element counter) and
resolved through a registry keyed by UIA `RuntimeId`. A new snapshot reuses the
existing ref for an element whose RuntimeId still resolves, so the model can hold a
ref across several snapshot/act cycles and only re-snapshot on `stale_ref`. Cheaper
for the model than re-numbering every node each time, and it makes `diff_since`
meaningful: generations version *snapshots*, not element identity.

## `interactive_only` keeps readable text nodes

The pruning rule is "drop nodes that can neither be acted on nor read with value",
not "keep only controls". Readable text nodes (static text, document text, list item
names) stay even in `interactive_only` snapshots, because verifying an action (the
default loop's diff step) needs the text that changed, not just the buttons.

Visibility pruning suppresses an offscreen or empty-bounds node, not its descendants.
WinUI flyouts can have layout-only ancestors with no rectangle while their menu
commands have visible bounds. Dropping the entire ancestor subtree hid Notepad's
Save-as command despite its presence in the single cached fetch.

## Image frames + `image_id`: one coordinate language for the model

The model works exclusively in *image pixels* of the most recent image; the server
converts to physical pixels via the frame's mapping (`Scale`, origin) and rejects
points outside the frame (`bad_coordinates` → "take a new screenshot"). Tree/window
bounds and mapping origins remain physical-pixel metadata; action `x,y` never use
those tree coordinates directly. The same rule applies to screenshots and zooms;
`image_id` disambiguates actions against an older image.

## Marks are drawn after downscale

Screenshots are downscaled to `maxScreenshotEdge` for vision input. Mark badges are
drawn **after** downscaling, in image pixel space, from the interactive elements of
the last snapshot that intersect the captured region. Drawing before downscale would
blur or lose 1-px badges at 300 % DPI, and anti-aliased marks make mark→pixel
round-trips lossy.

## Native input completion and common dialogs

`SendInput` success means queued, not consumed by the app. An immediately following
UIA Invoke overtook typed text in live Notepad, leaving only its first characters.
Typing, keyboard-fallback fill and chord completion now include a cancellable
100 ms processing grace period, matching FlaUI's input-processing heuristic without
blocking cancellation. It is not an OS queue barrier; callers must still verify.
Chord/drag holds use monotonic deadlines rather than adding requested sleep slices.

A common Save-as dialog displayed and reported a `ValuePattern`-set filename as
verified but saved under the old name, including when Save was invoked directly.
The acceptance flow therefore discovers that field by UIA and uses native Ctrl+A,
typing and Enter. This is an existing input route, not an app-specific server
special case or a claim that displayed-value verification proves application state.

## Pointer safety and windows nobody owns

Synthetic mouse input goes to whatever window is topmost at the point. A ref or mark
names an element in one window, so a real mouse click, wheel scroll or drag endpoint
first brings that window forward and then requires the window under the point to be
the target or a window it owns; otherwise the tool returns `occluded` and sends
nothing. Found live: a right-click by ref on a covered Explorer item landed on the
Calculator in front of it, an app the access check had never looked at. Pattern
actions (Invoke, Toggle, …) need no pointer and are unaffected.

UIA nests owned popups under their owner, so most menus and dialogs appear in the
owner's snapshot. Classic `#32768` menus without that owner (Explorer's "Show more
options", tray-icon menus) hang off the desktop root instead and were invisible to
every tool. They and the taskbar are untitled tool windows, so `windows_list_windows`
now lists exactly those two classes with synthesized titles, and a snapshot names any
open un-owned menu in its header. Process-name and pid specs still prefer a real
application window over these always-topmost surfaces. Other tool windows stay
unlisted: tooltips, IME and helper windows would be noise.

## Approval surface: native topmost dialogs; `prompts: deny` for headless

The human approval surface is a native topmost Win32 dialog — deliberately, because
the model cannot answer it. Any in-band approval (a tool that returns "approve?")
would make the same model its own gatekeeper; a modal dialog only a human at the
keyboard can press is an enforcement boundary, not a convention. In headless uses
(CI, service contexts) `prompts: deny` swaps the prompter for one that refuses every
request with an actionable error, so nothing silently auto-approves.

## Kill switch: hotkey → MCP notification → OMP extension `ctx.abort()`

The kill switch chain is: `RegisterHotKey` (on the server's UI thread) →
`SafetyGate.KillSwitch` (abort token, release held input, revoke control grants) →
MCP notification `notifications/windows/kill_switch` → the OMP extension
(`harness/omp/extensions/windows-computer-use.ts`) calls `ctx.abort()`, warns the
user, and queues a next-turn note instructing the model to stop. Enforcing the abort
*outside* the server matters because a server-side "stop" can only stop tools; the
harness owns the agent loop. A tool deadline cancels the same worker token as a
client abort or hotkey. A blocked provider cannot be forcibly interrupted, so the
registry returns without waiting indefinitely; every later input checks cancellation.
On a kill, a cooperative batch gets at most 50 ms to return its partial ledger.
The response owner audits the returned outcome, never a late worker completion.

## `windows_list_windows` is ungated

Listing top-level windows reveals titles and pids but grants no control and no
content; it is the discovery step that lets the model name the right app in its
access request. Gating it would force a grant just to ask for a grant.

## Monitor captures redact blocked windows

A screen capture would otherwise expose pixels of a password manager or UAC prompt.
Monitor and region captures blank blocked windows. The same rule applies when a
window capture or zoom uses screen pixels after `PrintWindow` fails; real
`PrintWindow` captures do not contain occluding windows and need no such redaction.

## YOLO mode

A user requirement for fire-and-forget automation on non-critical machines (VMs,
test boxes): auto-grant `control` to every app including blocked ones, skip
confirmation gates, the user-input pause and the password guard. Deliberately kept
narrow: the kill switch, focus safety, stale-ref checks, the audit log and the
overlay stay on. The skill skips normal-mode approval prompts inside the delegated
non-critical task, but preserves task scope, verification and kill-switch rules.

Normal-mode allowlist keys are normalized once when safety state is created,
including dictionaries loaded from JSON. A kill downgrades configured control
grants in memory for this session without rewriting the config file; persisted
human grants are downgraded in the grants file.


## Harness terminal detection

`blockHarnessTerminal` records the server's parent-process chain and refuses windows
belonging to those process IDs. The agent's own terminal is blocked by default so
desktop input cannot control its supervision. This is an app-automation guard, not
a sandbox against the shell or filesystem tools that the same user already owns.

## UIA2 per app

UIA3 covers nearly everything, but some WinForms apps misreport patterns under UIA3.
`uia2Apps` switches the automation provider per process name rather than globally —
a global UIA2 default would lose patterns the newer apps need.

## Isolation: sandbox and separate user — deprioritised

The strongest version of this safety story runs the agent in Windows Sandbox or a
separate non-admin user, where the desktop being automated cannot reach the real
user's session. Both are deprioritised: a mapped-folder `.wsb` and a separate-user
runbook are provided under [`sandbox/`](sandbox/README.md), but **unverified on the
development machine** (Windows Sandbox is disabled there and enabling it needs a
reboot). They are documented as provided-not-proven rather than recommended defaults.

## Parsec / remote sessions out of scope

Remote-session software (Parsec, RDP, cloud gaming overlays) injects its own input
and scaling layers; guaranteeing correct coordinates and hotkey delivery across them
is out of scope. Works-with-your-luck, unsupported.

## Build process: parallel slices, sequential verification

0.3.0 was built as parallel slices (perception, screenshot, actions, safety, harness
docs) in separate worktrees against a frozen contract (`ToolBase`, `Services`,
registry, frame store, gate), then merged and verified sequentially. The contract
froze names, error codes and cross-slice touch points so slices could not drift;
verification (desktop/integration tests, a real 300 % DPI two-monitor run) happens
after the merge, on the coordinator's machine, not in CI — CI builds the solution and
runs unit tests only, because desktop tests need an interactive session.

## Relationship to OMP's built-in `computer` prelude

OMP ships its own `computer` Eval prelude (disabled by default, `computer.enabled:
false`). This server is a separate, independent surface: ref-based and UIA-driven
where the prelude is its own mechanism, and safety-gated server-side regardless of
harness settings. Nothing in this repo enables or depends on the prelude; if both are
enabled they simply expose different tool sets, and the skill's routing rule (route
first, GUI last) applies to both.

## Acceptance routing deviation

The 50-file OMP routing scenario used the specialized file-edit tool's move
operation, not a PowerShell rename loop: this harness requires specialized file
tools for filesystem mutations. All 50 destination contents were checked and no
Explorer automation was used. This preserves the spec's non-GUI-first principle
without pretending its literal shell example was executed.

Separate same-main-model OMP runs mounted all 25 tools, ignored and reported an
on-screen injected instruction, and handled the real native hotkey notification
by aborting the agent turn and stopping further desktop actions. Automated input
state tests do not substitute for a human physically moving the mouse; remaining
environment limits are explicit in the README.

