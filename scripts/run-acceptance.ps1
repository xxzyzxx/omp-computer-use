# run-acceptance.ps1 — build the solution, run unit tests, then integration tests milestone by milestone.
# Milestones: M1 (Calculator), M2 (Notepad, Settings), M3 (DPI), M4 (Marks), M5 (Explorer batch), M6 (Safety).
# Usage: ./scripts/run-acceptance.ps1 [-Milestone M3] [-ContinueOnFailure]

param(
    [Parameter(Mandatory = $false)]
    [ValidateSet("M1", "M2", "M3", "M4", "M5", "M6")]
    [string]$Milestone = "",

    [Parameter(Mandatory = $false)]
    [switch]$ContinueOnFailure
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$unitTests = Join-Path $repoRoot "tests\FlaUI.Mcp.Tests"
$integrationTests = Join-Path $repoRoot "tests\FlaUI.Mcp.IntegrationTests"

function Write-Milestone($name, $passed, $output) {
    $tag = if ($passed) { "PASS" } else { "FAIL" }
    Write-Host "[$name] $tag"
    Write-Host ($output -join [Environment]::NewLine)
}

# 1. Build the solution.
Write-Host "=== Building solution ==="
$build = dotnet build (Join-Path $repoRoot "FlaUI.Mcp.sln") --nologo 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "=== Build FAILED ==="
    Write-Host ($build -join [Environment]::NewLine)
    exit 1
}
Write-Host "=== Build OK ==="

# 2. Run unit tests.
Write-Host "=== Running unit tests ==="
$unitOut = dotnet test $unitTests --no-build --nologo 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "=== Unit tests FAILED ==="
    Write-Host ($unitOut -join [Environment]::NewLine)
    exit 1
}
Write-Host "=== Unit tests OK ==="
Write-Host ($unitOut -join [Environment]::NewLine)

# 3. Integration tests.
$integrationTestsCsproj = Join-Path $integrationTests "FlaUI.Mcp.IntegrationTests.csproj"
$milestones = if ($Milestone) { @($Milestone) } else { @("M1", "M2", "M3", "M4", "M5", "M6") }
$failed = $false
foreach ($name in $milestones) {
    $integrationOutput = dotnet test $integrationTestsCsproj --no-build --nologo --filter "FullyQualifiedName~${name}_" 2>&1
    $code = $LASTEXITCODE
    Write-Milestone $name ($code -eq 0) $integrationOutput
    if ($code -ne 0) {
        $failed = $true
        if (-not $ContinueOnFailure) {
            Write-Host "=== Stopping at the first failure ==="
            exit 1
        }
    }
}

if ($failed) {
    Write-Host "=== Integration tests FAILED ==="
    exit 1
}
Write-Host "=== Integration tests OK ==="
