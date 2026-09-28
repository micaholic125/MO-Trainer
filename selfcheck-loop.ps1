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
$root = $PSScriptRoot
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

    $passed = 0
    $failed = 0

    # Take the LAST "N <word>, M <word>" pair: the self test prints its summary at the very end,
    # and earlier lines can contain similar number pairs (which is why a first match reported 0/0).
    $all = [regex]::Matches($output, '(\d+)\s*\S+\s*[,\uFF0C]\s*(\d+)\s*\S+')
    $match = if ($all.Count -gt 0) { $all[$all.Count - 1] } else { $null }
    if ($match -ne $null) {
        $passed = [int]$match.Groups[1].Value
        $failed = [int]$match.Groups[2].Value
    }

    if ($match -eq $null) {
        Write-Host ("[{0}] run {1}: could not parse the summary line -- stopped" -f $stamp, $run) -ForegroundColor Red
        Write-Host $output
        exit 2
    }

    if ($failed -gt 0) {
        Write-Host ("[{0}] run {1}: {2} passed, {3} FAILED -- stopped" -f $stamp, $run, $passed, $failed) -ForegroundColor Red
        ($output -split "`r?`n") | Where-Object { $_ -match 'FAIL' } | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
        exit 3
    }

    Write-Host ("[{0}] run {1}: {2} passed, 0 failed" -f $stamp, $run, $passed) -ForegroundColor Green

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
