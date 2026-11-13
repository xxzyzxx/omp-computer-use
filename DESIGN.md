# Design notes — Windows computer-use MCP (0.3.0)

Decisions and deviations from the obvious implementation, with reasons. The tool
surface and config keys are documented in the README; this file explains the *why*.

## Capture: PrintWindow, not Windows.Graphics.Capture

Window capture uses `PrintWindow` with `PW_RENDERFULLCONTENT`, falling back to a
bitblt of the screen region, and only falls back further to monitor capture when the
window is occluded or blank. Windows.Graphics.Capture (WGC) captures faster and can
see DirectX-exclusive content, but it requires a WinRT object graph inside a Win32
service process, pops a permission prompt on some builds, and its frame pool wants a
pump we would have to feed forever. `PrintWindow` with the full-content flag handles
the apps we actually target (Win32, WinForms, WPF, Chromium) in-process with no
prompt. Blank frames from hardware overlays are a known limit, surfaced as
`window_empty`/blank-detection rather than silently returning a black PNG.

## Snapshot: one UIA Subtree cache fetch

Snapshot walks the tree with a `CacheRequest` scoped to the subtree (elements,
properties, patterns) instead of per-node round-trips. At 300 % DPI and hundreds of
nodes, per-node property reads dominate wall time; a single cached subtree fetch
roughly an order-of-magnitude's less UIA traffic. The cost is that the cache is a
point-in-time view — which is exactly what a snapshot should be.

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

## Image frames + `image_id`: one coordinate language for the model

The model works exclusively in *image pixels* of the most recent image; the server
converts to physical pixels via the frame's mapping (`Scale`, origin) and rejects
points outside the frame (`bad_coordinates` → "take a new screenshot"). Physical
pixels never cross the protocol. This keeps the coordinate rule one sentence long in
the skill, and it is the same rule for screenshots and zooms; `image_id` disambiguates
when the model wants to act against an older image.

## Marks are drawn after downscale

Screenshots are downscaled to `maxScreenshotEdge` for vision input. Mark badges are
drawn **after** downscaling, in image pixel space, from the interactive elements of
the last snapshot that intersect the captured region. Drawing before downscale would
blur or lose 1-px badges at 300 % DPI, and anti-aliased marks make mark→pixel
round-trips lossy.

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
harness owns the agent loop, and OMP's default `tools.approvalMode` is `yolo`, so
without the extension nothing would stop the next turn. The `/windows-killswitch`
slash command is the manual fallback for when the hotkey is taken or the session is
remote.

## `windows_list_windows` is ungated

Listing top-level windows reveals titles and pids but grants no control and no
content; it is the discovery step that lets the model name the right app in its
access request. Gating it would force a grant just to ask for a grant.

## Monitor captures redact blocked windows

A full-monitor screenshot would otherwise exfiltrate the pixels of a password
manager or UAC prompt — exactly the apps the blocklist protects. Monitor and
"all-monitor" captures therefore blank blocked-app windows (same enforcement as
control, applied to perception).

## YOLO mode

A user requirement for fire-and-forget automation on non-critical machines (VMs,
test boxes): auto-grant `control` to every app including blocked ones, skip
confirmation gates, the user-input pause and the password guard. Deliberately kept
narrow: the kill switch, focus safety, stale-ref checks, the audit log and the
overlay stay on. It changes *who approves*, not the operating rules — the skill
states that verification and the kill-switch rules still apply.

## Harness terminal detection

`blockHarnessTerminal` walks the parent-process chain from the server process and
adds every ancestor exe to the blocklist. The agent's own terminal is the one window
it can do real damage with (typing into the harness, killing its own supervision), so
it is blocked by default. Parent-chain detection catches terminals launched under
different names (VS Code, Windows Terminal, conhost) without enumerating them.

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
