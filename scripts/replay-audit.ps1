#Requires -Version 5.1
<#
.SYNOPSIS
  Renders a WindowsComputerUse audit log (audit-*.jsonl) as a readable timeline.

.PARAMETER Path
  Audit JSONL file. Defaults to the newest audit-*.jsonl in %LOCALAPPDATA%\WindowsComputerUse\audit.

.PARAMETER Session
  Only entries with this session id (first 12 hex chars of the server run).

.PARAMETER Follow
  Keep tailing the file and print new lines as they appear (Ctrl+C to stop).

.EXAMPLE
  .\replay-audit.ps1                          # newest audit file
  .\replay-audit.ps1 -Session abc123def456    # one server run
  .\replay-audit.ps1 -Follow -Path .\audit-2026-01-02.jsonl
#>
[CmdletBinding()]
param(
    [string]$Path,
    [string]$Session,
    [switch]$Follow
)

$ErrorActionPreference = 'Stop'

if (-not $Path) {
    $dir = Join-Path $env:LOCALAPPDATA 'WindowsComputerUse\audit'
    $newest = Get-ChildItem -Path $dir -Filter 'audit-*.jsonl' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $newest) {
        Write-Error "No audit-*.jsonl found in $dir. Pass -Path <file> or set auditDir in config.json."
        return
    }
    $Path = $newest.FullName
}
if (-not (Test-Path $Path)) {
    Write-Error "Audit file not found: $Path"
    return
}

function Format-Args([object]$Arguments) {
    # Do not name this parameter $Args: PowerShell reserves that automatic variable.
    if ($null -eq $Arguments) { return '' }
    $parts = foreach ($p in $Arguments.PSObject.Properties) {
        $v = $p.Value
        if ($v -is [System.Management.Automation.PSCustomObject]) {
            $v = '{...}'
        } elseif ($v -is [array]) {
            $v = '[' + (($v | Select-Object -First 3 | ForEach-Object { "$_" }) -join ',') + $(if ($v.Count -gt 3) { ',...' }) + ']'
        } else {
            $s = "$v"
            if ($s.Length -gt 40) { $s = $s.Substring(0, 37) + '...' }
            $v = $s
        }
        "$($p.Name)=$v"
    }
    return ($parts -join ' ')
}

function Format-Entry([object]$e) {
    $timestamp = if ($e.ts -is [datetime]) { $e.ts } else {
        [datetime]::Parse($e.ts, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind)
    }
    $time = $timestamp.ToLocalTime().ToString('HH:mm:ss.fff')
    if ($e.event) {
        $detail = if ($e.details) { ($e.details | ConvertTo-Json -Compress -Depth 3) } else { '' }
        return "{0}  == {1} ==  {2}" -f $time, $e.event.ToUpperInvariant(), $detail
    }
    $tool = $e.tool
    $app = $e.target_app
    if (-not $app) { $app = '-' }
    $status = if ($e.code) { $e.code }
        elseif ($e.status) { $e.status }
        elseif ($e.is_error) { 'error' }
        elseif ($e.ok) { 'ok' }
        else { 'ok' }
    $argsSummary = Format-Args $e.args
    $sha = if ($e.screenshot_sha256) { [string]$e.screenshot_sha256 } else { '' }
    $shaPrefix = if ($sha) { $sha.Substring(0, [Math]::Min(12, $sha.Length)) } else { '-' }
    return "{0}  {1,-24} {2,-22} {3,-12} {4,6} ms  img {5,-12}  {6}" -f `
        $time, $tool, $app, $status, [int]$e.duration_ms, $shaPrefix, $argsSummary
}

$entries = New-Object System.Collections.Generic.List[object]
function Show-Entry([object]$e) {
    if ($Session -and "$($e.session)" -ne $Session) { return }
    $entries.Add($e)
    Write-Host (Format-Entry $e)
}

Write-Host "audit: $Path"
Get-Content -Path $Path | ForEach-Object {
    if ([string]::IsNullOrWhiteSpace($_)) { return }
    try { Show-Entry ($_ | ConvertFrom-Json) } catch { Write-Host "  (unparseable line: $($_.Exception.Message))" }
}

if ($Follow) {
    Write-Host "  ... following (Ctrl+C to stop) ..."
    Get-Content -Path $Path -Wait -Tail 0 | ForEach-Object {
        if ([string]::IsNullOrWhiteSpace($_)) { return }
        try { Show-Entry ($_ | ConvertFrom-Json) } catch { Write-Host "  (unparseable line: $($_.Exception.Message))" }
    }
}

# summary footer
$shown = $entries | Where-Object { -not $_.event }
Write-Host ''
Write-Host ('= summary: {0} tool calls' -f $shown.Count)
$shown | Group-Object tool | Sort-Object Count -Descending | ForEach-Object {
    $ok = ($_.Group | Where-Object { $_.ok -and -not $_.is_error -and -not $_.code -and -not $_.status }).Count
    Write-Host ('  {0,-28} {1,4} calls ({2} ok)' -f $_.Name, $_.Count, $ok)
}
$apps = $shown | Where-Object { $_.target_app } | Group-Object target_app | Sort-Object Count -Descending
if ($apps) {
    Write-Host '  by target app:'
    $apps | ForEach-Object { Write-Host ('    {0,-28} {1,4} calls' -f $_.Name, $_.Count) }
}
$events = $entries | Where-Object { $_.event }
if ($events) {
    Write-Host ('  events: ' + (($events | ForEach-Object { $_.event }) -join ', '))
}
