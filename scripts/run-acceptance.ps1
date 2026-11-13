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
$srcDir = Join-Path $repoRoot "src\FlaUI.Mcp"
$unitTests = Join-Path $repoRoot "tests\FlaUI.Mcp.UnitTests"
$integrationTests = Join-Path $repoRoot "tests\FlaUI.Mcp.IntegrationTests"

function Write-Milestone($name, $passed, $output) {
    $tag = if ($passed) { "PASS" } else { "FAIL" }
    Write-Host "[$name] $tag"
    if (-not $passed) {
        Write-Host $output
    }
}

# 1. Build the solution.
Write-Host "=== Building solution ==="
$build = dotnet build $repoRoot 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "=== Build FAILED ==="
    Write-Host $build
    exit 1
}
Write-Host "=== Build OK ==="

# 2. Run unit tests.
Write-Host "=== Running unit tests ==="
$unitOut = dotnet test $unitTests --no-build --nologo 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "=== Unit tests FAILED ==="
    Write-Host $unitOut
    exit 1
}
Write-Host "=== Unit tests OK ==="

# 3. Integration tests.
$integrationTestsCsproj = Join-Path $integrationTests "FlaUI.Mcp.IntegrationTests.csproj"
$dotnetIntegrationTests = Join-Path $integrationTests "bin"
dotnet build $integrationTestsCsproj --no-restore 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "=== Integration tests build FAILED ==="
    exit 1
}

$integrationResults = @()
$dotnetTest = Join-Path $env:TEMP "dotnet-test"
$integrationOutput = dotnet test $integrationTestsCsproj --no-build --nologo --filter "FullyQualifiedName~M1_" 2>&1
$integrationResults += [PSCustomObject]@{ Name = "M1"; Output = $integrationOutput; ExitCode = $LASTEXITCODE }
$integrationOutput = dotnet test $integrationTestsCsproj --no-build --nologo --filter "FullyQualifiedName~M2_" 2>&1
$integrationResults += [PSCustomObject]@{ Name = "M2"; Output = $integrationOutput; ExitCode = $LASTEXITCODE }
$integrationOutput = dotnet test $integrationTestsCsproj --no-build --nologo --filter "FullyQualifiedName~M3_" 2>&1
$integrationResults += [PSCustomObject]@{ Name = "M3"; Output = $integrationOutput; ExitCode = $LASTEXITCODE }
$integrationOutput = dotnet test $integrationTestsCsproj --no-build --nologo --filter "FullyQualifiedName~M4_" 2>&1
$integrationResults += [PSCustomObject]@{ Name = "M4"; Output = $integrationOutput; ExitCode = $LASTEXITCODE }
$integrationOutput = dotnet test $integrationTestsCsproj --no-build --nologo --filter "FullyQualifiedName~M5_" 2>&1
$integrationResults += [PSCustomObject]@{ Name = "M5"; Output = $integrationOutput; ExitCode = $LASTEXITCODE }
$integrationOutput = dotnet test $integrationTestsCsproj --no-build --nologo --filter "FullyQualifiedName~M6_" 2>&1
$integrationResults += [PSCustomObject]@{ Name = "M6"; Output = $integrationOutput; ExitCode = $LASTEXITCODE }

# Stop at the first failure unless -ContinueOnFailure.
$failed = $false
foreach ($result in $integrationResults) {
    Write-Milestone $result.Name ($result.ExitCode -eq 0) $result.Output
    if ($result.ExitCode -ne 0) {
        $failed = $true
        if (-not $ContinueOnFailure) {
            Write-Host "=== Stopping at the first failure ==="
            break
        }
    }
}

# 4. Cleanup.
Remove-Item -Path $dotnetTest -Recurse -Force -ErrorAction SilentlyContinue

if ($failed) {
    Write-Host "=== Integration tests FAILED ==="
    exit 1
}
Write-Host "=== Integration tests OK ==="
