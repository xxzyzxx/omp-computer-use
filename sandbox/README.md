# Isolated desktop for the agent (provided, not verified)

The strongest version of the safety story runs the agent's *whole* desktop inside a
disposable environment, so the windows being automated cannot reach your real
session. Two options are described here. **Neither has been verified on the
development machine** — Windows Sandbox is disabled there (enabling it requires a
reboot), and the separate-user variant was not exercised. Treat both as runbooks to
validate yourself, not as tested defaults.

## Option A: Windows Sandbox

Windows Sandbox is a disposable, full Windows desktop that discards everything on
close. Enable it once (admin, then reboot):

```powershell
Enable-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM
```

Then:

1. Publish the server: `./scripts/publish.ps1` → `publish/win-x64/FlaUI.Mcp.exe`.
2. Adjust `HostFolder` in `sandbox/WindowsComputerUse.wsb` to your checkout's
   `publish/win-x64` path, then run the `.wsb`. It maps the folder read-only to
   `C:\WindowsComputerUse` inside the sandbox, enables networking, and opens
   PowerShell there on logon.
3. Install OMP inside the sandbox and point its `~/.omp/agent/mcp.json` at the
   mapped exe:

   ```json
   {
     "mcpServers": {
       "windows": {
         "type": "stdio",
         "command": "C:\\WindowsComputerUse\\FlaUI.Mcp.exe",
         "args": [],
         "timeout": 200000
       }
     }
   }
   ```

4. Use omp inside the sandbox as usual. Everything the agent does happens on the
   sandbox desktop; closing the sandbox discards it.

Notes: the sandbox needs Windows Pro/Enterprise; networking is enabled here so omp
can reach its model provider — audit what the agent does before trusting that.
The skill/extension install steps from the main README apply inside the sandbox too
(copy them into the sandbox's agent dir, or map the repo read-only as a second
folder and run `scripts/install-omp.ps1` there).

## Option B: separate non-admin local user

Alternative without virtualization: create a standard (non-admin) local user, sign
in to that desktop interactively, and run omp + the server there. The agent can
only touch that user's session and cannot elevate.

1. Create the user and keep them out of Administrators:
   ```powershell
   net user agent-agent "Passphrase-Here" /add
   net localgroup Administrators agent-agent /delete   # only if it was added
   ```
2. Sign in as that user (Win+L → switch user), install OMP and the published server
   in *that* profile, run `scripts/install-omp.ps1` there.
3. Do your real work in your own session; the agent's desktop is the other user's.

Caveats: you must physically switch sessions (Windows locks the previous session's
desktop), and anything the agent needs (licenses, sign-ins, files) must exist in the
agent user's profile.

## Which to pick

Sandbox: disposable and clean, but no persistence and some overhead. Separate user:
persistent and lightweight, but not disposable. Neither replaces the server-side
safety layer — grants, the kill switch, blocked apps and the audit log apply in
every case.
