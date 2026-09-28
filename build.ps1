<#
    MO-Trainer build script
    Offline build: uses the .NET Framework in-box csc.exe (no SDK, no NuGet, no network).

    Usage:
      powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
      powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 -Test -RunTest
      powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 -Clean
      powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 -NoKill

    The built exe is locked while the trainer is running. Rather than emitting a
    second binary under a different name, this script closes the running instance
    and republishes to the same path. Pass -NoKill to refuse and fail instead.
#>
[CmdletBinding()]
param(
    [switch]$Clean,
    [switch]$Test,
    [switch]$RunTest,
    [switch]$NoKill
)

$ErrorActionPreference = 'Stop'

# NOTE: messages are intentionally ASCII-only. This file has no BOM, and
# Windows PowerShell 5.1 would read non-ASCII text using the ANSI code page.

$root = $PSScriptRoot
if (-not $root) { $root = Split-Path -Parent $MyInvocation.MyCommand.Definition }

$appName = 'MentalOmegaTrainer'

# Running the built app locks its exe, which makes csc fail with CS0016.
function Test-FileLocked([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    try {
        $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
        $stream.Close()
        return $false
    } catch {
        return $true
    }
}

# Close the running trainer so we can overwrite its exe.
# This is safe: all calibration state lives in dist\config\profile.json and is
# written on every 'save' action, so nothing is lost by closing the window.
function Close-RunningApp([string]$Name) {
    $procs = @(Get-Process -Name $Name -ErrorAction SilentlyContinue)
    if ($procs.Count -eq 0) { return 0 }

    # Ask nicely first (lets the window run its normal shutdown path)...
    foreach ($p in $procs) {
        try { [void]$p.CloseMainWindow() } catch { }
    }
    Start-Sleep -Milliseconds 900

    # ...then force whatever is left (a tray-only window ignores CloseMainWindow).
    $left = @(Get-Process -Name $Name -ErrorAction SilentlyContinue)
    foreach ($p in $left) {
        try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch { }
    }

    # Wait for the OS to actually drop the file handle.
    for ($i = 0; $i -lt 20; $i++) {
        if (@(Get-Process -Name $Name -ErrorAction SilentlyContinue).Count -eq 0) { break }
        Start-Sleep -Milliseconds 100
    }
    return $procs.Count
}

function Wait-FileFree([string]$Path, [int]$TimeoutMs) {
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMs)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (-not (Test-FileLocked $Path)) { return $true }
        Start-Sleep -Milliseconds 150
    }
    return (-not (Test-FileLocked $Path))
}

$srcDir     = Join-Path $root 'src'
$outDir     = Join-Path $root 'dist'
$manifest   = Join-Path $srcDir 'app.manifest'
$exeMain    = Join-Path $outDir 'MentalOmegaTrainer.exe'
$exeTest    = Join-Path $outDir '_selftest.exe'

Write-Host '=== MO-Trainer build ===' -ForegroundColor Cyan
Write-Host ("root     : {0}" -f $root)

# ---------------------------------------------------------------- compiler
$csc = $null
$candidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
foreach ($c in $candidates) {
    if (Test-Path -LiteralPath $c) { $csc = $c; break }
}
if (-not $csc) { throw 'csc.exe not found. .NET Framework 4.x is required.' }
Write-Host ("compiler : {0}" -f $csc)

if (-not (Test-Path -LiteralPath $srcDir))     { throw ("source dir not found: {0}" -f $srcDir) }
if (-not (Test-Path -LiteralPath $manifest))   { throw ("manifest not found: {0}" -f $manifest) }

if ($Clean -and (Test-Path -LiteralPath $outDir)) {
    Remove-Item -LiteralPath $outDir -Recurse -Force
    Write-Host 'clean    : dist\ removed'
}
if (-not (Test-Path -LiteralPath $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

# Remove the legacy "second binary" output. Producing two exes made it ambiguous
# which one was current - it caused real confusion, so that path is gone for good.
$legacy = Join-Path $outDir 'MentalOmegaTrainer.new.exe'
if (Test-Path -LiteralPath $legacy) {
    Remove-Item -LiteralPath $legacy -Force -ErrorAction SilentlyContinue
    Write-Host 'cleanup  : removed stale MentalOmegaTrainer.new.exe' -ForegroundColor Yellow
}

# ---------------------------------------------------------------- free the exe
if (Test-FileLocked $exeMain) {
    if ($NoKill) {
        Write-Host 'error    : dist\MentalOmegaTrainer.exe is locked by a running instance.' -ForegroundColor Red
        Write-Host '           close the trainer, then re-run (or drop -NoKill to let this script close it).' -ForegroundColor Red
        exit 1
    }

    Write-Host 'prep     : exe is locked, closing the running trainer...' -ForegroundColor Yellow
    $closed = Close-RunningApp $appName

    if (-not (Wait-FileFree $exeMain 5000)) {
        Write-Host ('error    : still locked after closing {0} instance(s).' -f $closed) -ForegroundColor Red
        Write-Host '           The trainer runs elevated, so this script needs an elevated shell' -ForegroundColor Red
        Write-Host '           to close it. Re-run as administrator, or close it by hand.' -ForegroundColor Red
        exit 1
    }
    Write-Host ('prep     : closed {0} instance(s); exe is free' -f $closed) -ForegroundColor Yellow
}

# ---------------------------------------------------------------- sources
$allSources = @(Get-ChildItem -LiteralPath $srcDir -Recurse -Filter '*.cs' | Sort-Object FullName | ForEach-Object { $_.FullName })
if ($allSources.Count -eq 0) { throw 'no .cs source files found' }

$mainSources = @($allSources | Where-Object { (Split-Path -Leaf $_) -ne 'SelfTest.cs' })
Write-Host ("sources  : {0} files ({1} for main build)" -f $allSources.Count, $mainSources.Count)

# /codepage:65001 is mandatory: our .cs files are UTF-8 and the in-box
# compiler would otherwise fall back to the system ANSI code page and
# corrupt every Chinese string literal.
$commonArgs = @(
    '/nologo',
    '/warn:4',
    '/optimize+',
    '/debug-',
    '/codepage:65001',
    '/platform:x64',
    '/reference:System.dll',
    '/reference:System.Core.dll',
    '/reference:System.Drawing.dll',
    '/reference:System.Windows.Forms.dll'
)

$total = [System.Diagnostics.Stopwatch]::StartNew()
$failed = $false

# ---------------------------------------------------------------- main exe
Write-Host ''
Write-Host '--- building MentalOmegaTrainer.exe ---' -ForegroundColor Yellow
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$mainArgs = $commonArgs + @(
    '/target:winexe',
    ('/win32manifest:' + $manifest),
    ('/out:' + $exeMain)
) + $mainSources
& $csc $mainArgs
$rc = $LASTEXITCODE
$sw.Stop()
if ($rc -ne 0 -or -not (Test-Path -LiteralPath $exeMain)) {
    Write-Host ("MAIN BUILD FAILED (exit {0})" -f $rc) -ForegroundColor Red
    $failed = $true
} else {
    $size = (Get-Item -LiteralPath $exeMain).Length
    Write-Host ("main build ok: {0} bytes in {1} ms" -f $size, $sw.ElapsedMilliseconds) -ForegroundColor Green
}

# ---------------------------------------------------------------- selftest exe
if ($Test -and -not $failed) {
    Write-Host ''
    Write-Host '--- building _selftest.exe (console, no elevation) ---' -ForegroundColor Yellow
    $sw2 = [System.Diagnostics.Stopwatch]::StartNew()
    $testArgs = $commonArgs + @(
        '/target:exe',
        '/main:MoTrainer.Diagnostics.SelfTest',
        ('/out:' + $exeTest)
    ) + $allSources
    & $csc $testArgs
    $rc2 = $LASTEXITCODE
    $sw2.Stop()
    if ($rc2 -ne 0 -or -not (Test-Path -LiteralPath $exeTest)) {
        Write-Host ("SELFTEST BUILD FAILED (exit {0})" -f $rc2) -ForegroundColor Red
        $failed = $true
    } else {
        Write-Host ("selftest build ok in {0} ms" -f $sw2.ElapsedMilliseconds) -ForegroundColor Green
    }
}

$total.Stop()
Write-Host ''
if ($failed) {
    Write-Host ("BUILD FAILED ({0} ms)" -f $total.ElapsedMilliseconds) -ForegroundColor Red
    exit 1
}

Write-Host ("BUILD OK ({0} ms)" -f $total.ElapsedMilliseconds) -ForegroundColor Green
Write-Host ("output   : {0} ({1} bytes)" -f $exeMain, (Get-Item -LiteralPath $exeMain).Length)

if ($RunTest) {
    if (-not (Test-Path -LiteralPath $exeTest)) {
        Write-Host 'selftest exe not built; run with -Test first.' -ForegroundColor Red
        exit 1
    }
    Write-Host ''
    Write-Host '--- running selftest ---' -ForegroundColor Yellow
    & $exeTest
    $rcTest = $LASTEXITCODE
    Write-Host ''
    if ($rcTest -eq 0) {
        Write-Host 'SELFTEST PASSED' -ForegroundColor Green
    } else {
        Write-Host ("SELFTEST REPORTED PROBLEMS (exit {0})" -f $rcTest) -ForegroundColor Yellow
    }
    exit $rcTest
}

exit 0
