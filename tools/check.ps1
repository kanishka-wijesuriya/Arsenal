<#
.SYNOPSIS
    Everything a change should pass before it is merged: the Release build, the tests,
    every tool still building, and the harnesses that check themselves.

.DESCRIPTION
    Run from anywhere; paths are taken from this script's own folder. Exits non-zero on
    the first stage that fails, and prints the output of any harness that fails so the
    cause is on screen rather than lost.

    Harnesses left out, on purpose:
      driver-scan-smoke    asks ASUS's servers, so it depends on the network.
      update-feed-smoke    needs a release's signed feed and package as arguments.
      *-probe, scroll-fps, slash-preview-cost
                           measure rather than check; they have no pass or fail.
      icon-gen, store-package, store-listing-art
                           produce files.
    Harnesses that need the device simulator run only where it is compiled in, which is
    a Debug build; they are listed in $SimulatorHarnesses and skipped when absent.

.PARAMETER SkipHarnesses
    Build and test only.
#>
[CmdletBinding()]
param(
    [switch] $SkipHarnesses
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$Harnesses = @(
    'navigation-smoke', 'ui-controls-smoke', 'theme-smoke', 'driver-ui-smoke',
    'keyboard-preview-smoke', 'memory-lifetime-smoke', 'setup-smoke', 'slash-preview-smoke',
    'slider-range-smoke', 'telemetry-poll-smoke', 'ui-memory-smoke', 'update-card-smoke'
)
$SimulatorHarnesses = @('simulator-smoke', 'peripherals-smoke')

function Step([string] $name, [scriptblock] $action) {
    Write-Host "== $name" -ForegroundColor Cyan
    & $action
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $name" -ForegroundColor Red
        exit 1
    }
}

# Serial, as the release build is: parallel builds of this solution have raced on the
# shared obj folders.
Step 'Release build' { dotnet build Arsenal.slnx -c Release -m:1 -v q -nologo }
Step 'Tests' { dotnet test Arsenal.Tests -v q -nologo }
Step 'Tools build' { dotnet build Arsenal.Tools.slnx -v q -nologo }

if ($SkipHarnesses) { exit 0 }

$failed = @()
foreach ($name in $Harnesses + $SimulatorHarnesses) {
    $project = Get-ChildItem (Join-Path $root "tools\$name") -Filter *.csproj -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $project) {
        if ($SimulatorHarnesses -notcontains $name) { Write-Host "missing: $name" -ForegroundColor Red; $failed += $name }
        continue
    }

    $assembly = ([xml](Get-Content $project.FullName)).Project.PropertyGroup.AssemblyName | Where-Object { $_ } | Select-Object -First 1
    if (-not $assembly) { $assembly = [IO.Path]::GetFileNameWithoutExtension($project.Name) }
    $exe = Get-ChildItem (Join-Path $project.DirectoryName 'bin') -Recurse -Filter "$assembly.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $exe) { Write-Host "not built: $name" -ForegroundColor Red; $failed += $name; continue }

    # Run with no arguments, as each harness documents; some read their first argument
    # as a file to render to. Output is kept outside the source tree.
    $output = Join-Path ([IO.Path]::GetTempPath()) "arsenal-check\$name"
    New-Item -ItemType Directory -Force $output | Out-Null
    $log = Join-Path $output 'output.txt'

    $watch = [Diagnostics.Stopwatch]::StartNew()
    $process = Start-Process $exe.FullName -WorkingDirectory $output -PassThru -NoNewWindow `
        -RedirectStandardOutput $log -RedirectStandardError "$log.err"
    # Read the handle now: without it PowerShell reports no exit code for a process
    # started with redirected output.
    $null = $process.Handle
    if (-not $process.WaitForExit(240000)) {
        $process.Kill()
        Write-Host ("{0,-24} timed out" -f $name) -ForegroundColor Red
        $failed += $name
        continue
    }

    if ($process.ExitCode -eq 0) {
        Write-Host ("{0,-24} ok ({1:N0}s)" -f $name, $watch.Elapsed.TotalSeconds)
    } else {
        Write-Host ("{0,-24} FAILED with exit code {1}" -f $name, $process.ExitCode) -ForegroundColor Red
        Get-Content $log, "$log.err" -ErrorAction SilentlyContinue | Select-Object -Last 25 | ForEach-Object { "    $_" }
        $failed += $name
    }
}

if ($failed.Count -gt 0) {
    Write-Host "Failed: $($failed -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host 'All checks passed.' -ForegroundColor Green
