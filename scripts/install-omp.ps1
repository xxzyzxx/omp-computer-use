# Installs the Windows computer-use MCP server into the OMP agent directory:
# merges the `windows` entry into mcp.json, copies the skill and the kill-switch extension.
# Requires PowerShell 7 (pwsh) for ConvertFrom-Json -AsHashtable.
#
# Usage: ./scripts/install-omp.ps1 [-Yolo] [-Publish] [-ExePath <path>] [-AgentDir <dir>]
[CmdletBinding()]
param(
    # Start the server with --yolo (allow-all mode). Read the safety section of README.md first.
    [switch]$Yolo,
    # Run scripts/publish.ps1 first to (re)build publish/win-x64.
    [switch]$Publish,
    # Use this exe instead of publish/win-x64/FlaUI.Mcp.exe.
    [string]$ExePath,
    # OMP agent directory (default: ~/.omp/agent).
    [string]$AgentDir = "$env:USERPROFILE\.omp\agent"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

if ($Publish) {
    & (Join-Path $PSScriptRoot "publish.ps1")
    if ($LASTEXITCODE -ne 0) { throw "publish.ps1 failed" }
}

if (-not $ExePath) {
    $ExePath = Join-Path $repoRoot "publish/win-x64/FlaUI.Mcp.exe"
}
$ExePath = [System.IO.Path]::GetFullPath($ExePath)
if (-not (Test-Path $ExePath)) {
    throw "Server exe not found at $ExePath. Run ./scripts/publish.ps1 first or pass -ExePath."
}

# 1. Merge the `windows` entry into <AgentDir>\mcp.json, preserving every other server.
New-Item -ItemType Directory -Force -Path $AgentDir | Out-Null
$mcpPath = Join-Path $AgentDir "mcp.json"
if (Test-Path $mcpPath) {
    $mcp = Get-Content -Raw $mcpPath | ConvertFrom-Json -AsHashtable
} else {
    $mcp = @{}
}
if (-not $mcp.Contains("mcpServers")) {
    if ($mcp.Count -gt 0) {
        throw "$mcpPath does not contain a 'mcpServers' object; fix or remove it and re-run."
    }
    $mcp["mcpServers"] = @{}
} elseif (-not ($mcp["mcpServers"] -is [hashtable])) {
    throw "$mcpPath 'mcpServers' is not an object; fix or remove it and re-run."
}
if (-not $mcp.Contains('$schema')) {
    $mcp['$schema'] = "https://raw.githubusercontent.com/can1357/oh-my-pi/main/packages/coding-agent/src/config/mcp-schema.json"
}
$toolArgs = @()
if ($Yolo) { $toolArgs += "--yolo" }
$mcp["mcpServers"]["windows"] = [ordered]@{
    type    = "stdio"
    command = $ExePath
    args    = $toolArgs
    timeout = 200000
}
$mcp | ConvertTo-Json -Depth 10 | Set-Content -Encoding utf8 $mcpPath

# 2. Skill: matched by description, tells the model how to drive the windows_* tools.
$skillDir = Join-Path $AgentDir "skills/windows-computer-use"
New-Item -ItemType Directory -Force -Path $skillDir | Out-Null
Copy-Item (Join-Path $repoRoot "skills/windows-computer-use/SKILL.md") (Join-Path $skillDir "SKILL.md") -Force

# 3. Extension: turns the server's kill-switch notification into ctx.abort() + a user warning.
$extDir = Join-Path $AgentDir "extensions"
New-Item -ItemType Directory -Force -Path $extDir | Out-Null
Copy-Item (Join-Path $repoRoot "harness/omp/extensions/windows-computer-use.ts") (Join-Path $extDir "windows-computer-use.ts") -Force

Write-Host "Installed Windows computer-use MCP server for OMP:"
Write-Host "  exe:        $ExePath"
Write-Host "  mcp.json:   $mcpPath (server name: windows)"
Write-Host "  skill:      $skillDir\SKILL.md"
Write-Host "  extension:  $extDir\windows-computer-use.ts"
# The server also honours "yolo": true in its config file; report the mode it will really start in.
$configPath = if ($env:WINDOWS_MCP_CONFIG) { $env:WINDOWS_MCP_CONFIG } else { Join-Path $env:LOCALAPPDATA "WindowsComputerUse/config.json" }
$configYolo = $false
if (Test-Path $configPath) {
    try { $configYolo = [bool](Get-Content -Raw $configPath | ConvertFrom-Json).yolo } catch { }
}
if ($Yolo) {
    Write-Host "  mode:       --yolo (allow-all; read the safety section of README.md)"
} elseif ($configYolo) {
    Write-Host "  mode:       YOLO via `"yolo`": true in $configPath (allow-all, no dialogs)"
} else {
    Write-Host "  mode:       default (access grants + confirmation gates on)"
}
Write-Host ""
Write-Host "Next steps:"
Write-Host "  - Re-run this script with -Yolo to start the server with --yolo (allow-all), or without it to remove the --yolo argument."
Write-Host "  - Restart omp after a first install (skill + extension load at startup); /mcp reload suffices for later MCP-only changes. /mcp list confirms the 'windows' server connected."
Write-Host "  - Tools appear as mcp__windows_snapshot, mcp__windows_click, ... and xd://mcp__windows_* devices."
Write-Host "  - Kill switch: Ctrl+Alt+Pause on the controlled machine; the extension aborts the run and the server revokes grants."
