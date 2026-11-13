# Publishes the Windows computer-use MCP server as a self-contained folder.
# Usage: ./scripts/publish.ps1 [-Arm64] [-Configuration Release]
[CmdletBinding()]
param(
    # Publish for win-arm64 instead of win-x64.
    [switch]$Arm64,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$rid = if ($Arm64) { "win-arm64" } else { "win-x64" }
$output = Join-Path $repoRoot "publish/$rid"

dotnet publish (Join-Path $repoRoot "src/FlaUI.Mcp/FlaUI.Mcp.csproj") `
    -c $Configuration `
    -r $rid `
    --self-contained true `
    -p:PublishSingleFile=false `
    -o $output
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$exe = Join-Path $output "FlaUI.Mcp.exe"
Write-Host "Published $rid server:"
Write-Host $exe
