# selfcheck-loop.ps1 -- run the self test repeatedly until something is wrong or enough runs are done.
#
# NOTE: keep this file ASCII-only. Windows PowerShell 5.1 reads .ps1 as ANSI (GBK on zh-CN),
# so non-ASCII text here breaks the parser. Chinese comments belong in the C# sources, not here.
#
# Two real purposes (not "keep the machine busy"):
#   1. Catch regressions -- any failed check stops the loop and prints the failing lines.
#   2. With -Probe, also run the live probe (--autofind) every round so the in-game numbers
#      (object counts etc.) keep landing in the log and can be watched over time.
#
# Usage:
#   .\selfcheck-loop.ps1                  # 10 rounds
#   .\selfcheck-loop.ps1 -MaxRuns 0       # run forever (Ctrl+C to stop)
#   .\selfcheck-loop.ps1 -MaxRuns 50
#   .\selfcheck-loop.ps1 -Probe           # also probe the running game each round
#
# It does NOT change code and does NOT decide "the task is finished" -- those need a human.
# Its job is to watch and stop.

param(
    [int]$MaxRuns = 10,
    [int]$IntervalSeconds = 5,
    [switch]$Probe
)

$ErrorActionPreference = 'Stop'

# The self test prints UTF-8, but PowerShell 5.1 decodes native command output using
# the system ANSI code page (GBK on this machine), which turns Chinese punctuation into
# mojibake. Tell it to decode as UTF-8 so the captured text is actually readable.
# (We no longer *parse* Chinese -- see the SELFTEST_RESULT line below -- but the failure
# dump printed on a regression is useless if it comes out garbled.)
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

# This script lives in tools\, but every path it touches (src\, dist\) is one level
# up. Resolve the project root rather than assuming the script sits next to them.
$root = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path (Join-Path $root 'src'))) {
    # Fallback: also works if someone runs a copy from the project root.
    if (Test-Path (Join-Path $PSScriptRoot 'src')) { $root = $PSScriptRoot }
    else { throw ("project root not found (no src\ under {0} or its parent)" -f $PSScriptRoot) }
}
Set-Location $root

$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$src = @(Get-ChildItem -Path .\src -Recurse -Filter *.cs | ForEach-Object { $_.FullName })
$selftest = ".\dist\_selftest.exe"

$limit = if ($MaxRuns -eq 0) { 'unlimited' } else { $MaxRuns }
$probeState = if ($Probe) { 'on' } else { 'off' }
Write-Host "=== self check loop ===" -ForegroundColor Cyan
Write-Host ("sources: {0} files | max runs: {1} | interval: {2}s | probe: {3}" -f $src.Count, $limit, $IntervalSeconds, $probeState) -ForegroundColor DarkGray

Write-Host "compiling..." -ForegroundColor DarkGray
& $csc /nologo /warn:4 /optimize+ /debug- /codepage:65001 /platform:x64 /target:exe `
    /main:MoTrainer.Diagnostics.SelfTest /out:$selftest `
    /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll $src 2>&1 | Out-String | Write-Verbose

if ($LASTEXITCODE -ne 0) {
    Write-Host "COMPILE FAILED -- stopped (this needs fixing)" -ForegroundColor Red
    exit 1
}

$run = 0
while ($true) {
    $run++
    $stamp = Get-Date -Format 'HH:mm:ss'
    $output = & $selftest 2>&1 | Out-String
    $exit = $LASTEXITCODE

    # The exit code is the authority on pass/fail: it is a process-level signal and
    # cannot be corrupted by console encoding. The parsed numbers are only for display
    # (and for noticing "the check count dropped", which means assertions were lost).
    $passed = -1
    $failed = -1
    $warned = -1
    $m = [regex]::Match($output, 'SELFTEST_RESULT pass=(\d+) fail=(\d+) warn=(\d+)')
    if ($m.Success) {
        $passed = [int]$m.Groups[1].Value
        $failed = [int]$m.Groups[2].Value
        $warned = [int]$m.Groups[3].Value
    }

    if ($exit -ne 0) {
        $label = if ($m.Success) { ("{0} passed, {1} FAILED" -f $passed, $failed) } else { ("exit code {0}" -f $exit) }
        Write-Host ("[{0}] run {1}: {2} -- stopped" -f $stamp, $run, $label) -ForegroundColor Red
        ($output -split "`r?`n") | Where-Object { $_ -match 'FAIL' } | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
        exit 3
    }

    if (-not $m.Success) {
        # Exit code says OK but there is no machine-readable summary. This script compiles
        # the exe itself, so the usual cause is that src\Diagnostics\SelfTest.cs no longer
        # prints SELFTEST_RESULT (source older than this script expects, or the line was
        # removed). Do not just print "0 passed" and move on -- that hid a real bug once.
        Write-Host ("[{0}] run {1}: exit 0 but no SELFTEST_RESULT line -- stopped" -f $stamp, $run) -ForegroundColor Red
        Write-Host "    src\Diagnostics\SelfTest.cs does not print SELFTEST_RESULT; this script cannot read the counts."
        Write-Host $output
        exit 2
    }

    Write-Host ("[{0}] run {1}: {2} passed, 0 failed, {3} hints" -f $stamp, $run, $passed, $warned) -ForegroundColor Green

    if ($Probe) {
        try {
            $p = Start-Process -FilePath .\dist\MentalOmegaTrainer.exe -ArgumentList '--smoke --autofind' -PassThru
            Start-Sleep -Seconds 12
            if (-not $p.HasExited) { Write-Host "    probe still running (game maybe not attached)" -ForegroundColor Yellow }
        }
        catch {
            Write-Host ("    probe failed to start: {0}" -f $_.Exception.Message) -ForegroundColor Yellow
        }
    }

    if ($MaxRuns -gt 0 -and $run -ge $MaxRuns) {
        Write-Host ("done: {0} runs, all clean" -f $MaxRuns) -ForegroundColor Cyan
        break
    }

    Start-Sleep -Seconds $IntervalSeconds
}
