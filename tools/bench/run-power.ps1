<#
    tools/bench/run-power.ps1 -- resident power / leak benchmark
    (docs/12 section 4 / docs/41 D-14)

    Gate: single pet idle CPU <= 0.5% (one core), memory <= 150 MB,
          8h resident memory growth <= 5% (D-14d), no handle leak (M-09).

    This script only samples and judges; it does NOT launch the pet.
    For the 8-hour soak use -DurationSeconds 28800 with -TimeSeriesPath so the
    growth can be audited afterwards from the raw samples.

    Usage:
        Start-Process .\packages\core\PetDock.Core\bin\Release\net8.0-windows\PetDock.Core.exe
        & .\tools\bench\run-power.ps1
        & .\tools\bench\run-power.ps1 -DurationSeconds 60
        & .\tools\bench\run-power.ps1 -DurationSeconds 28800 -IntervalSeconds 60 `
              -TimeSeriesPath .dsh-code\soak-samples.csv

    Output:
        PROCESS <pid> cores=<n> interval=<s>s samples=<n> budget-cpu=<x>% budget-mem=<y>MB leak-budget=<z>%
        TIMESERIES <path>                       (only when -TimeSeriesPath is given)
        SAMPLES <n>
        CPU-PERCENT <x>
        PEAK-WORKINGSET-MB <x>
        PEAK-PRIVATE-MB <x>
        FIRST-WORKINGSET-MB <x>
        LAST-WORKINGSET-MB <x>
        MEM-GROWTH-PERCENT <x>
        PEAK-HANDLES <n>
        HND-GROWTH <n>
        BENCH-PASS | BENCH-FAIL (+ FAIL-CPU / FAIL-MEM / FAIL-LEAK lines)

    Exit codes: 0 pass / 1 fail / 2 no process / 3 process exited early / 4 zero elapsed

    Implementation notes (each paid for in this project's ledger):
      - cmdlets only (Get-Process / Measure-Object). Do not pipe a native
        program's output through Select-Object -First or Where-Object: that
        swallows error lines and corrupts the reported exit code.
      - Fixed sample interval. CPU = delta(TotalProcessorTime) / delta(wall) /
        logical cores * 100, i.e. "relative to one core", matching docs/41.
      - Leak growth is FIRST vs LAST working set, not peak vs peak: D-14d asks
        whether memory grows over 8h, and a peak-only view cannot answer that.
      - ASCII only. This file is executed by powershell 5.1, which reads a
        BOM-less .ps1 as ANSI; non-ASCII comments would be mangled into
        syntax errors or silently wrong regexes.
#>
[CmdletBinding()]
param(
    [string]$ProcessName = 'PetDock.Core',
    [int]$ProcessId = 0,
    [int]$DurationSeconds = 120,
    [double]$CpuBudgetPercent = 0.5,
    [int]$MemoryBudgetMb = 150,
    [int]$IntervalSeconds = 5,
    [double]$LeakBudgetPercent = 5.0,
    [string]$TimeSeriesPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$interval = [Math]::Max(1, $IntervalSeconds)
$samples = [Math]::Max(2, [int][Math]::Floor($DurationSeconds / $interval))

if ($ProcessId -gt 0) {
    $proc = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if (-not $proc) {
        Write-Output "ERROR no running process with pid $ProcessId"
        exit 2
    }
} else {
    $proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $proc) {
        Write-Output "ERROR no running process named '$ProcessName' - start PetDock first"
        exit 2
    }
}

$logicalCores = [Environment]::ProcessorCount
Write-Output "PROCESS $($proc.Id) cores=$logicalCores interval=${interval}s samples=$samples budget-cpu=$CpuBudgetPercent% budget-mem=${MemoryBudgetMb}MB leak-budget=$LeakBudgetPercent%"

$writer = $null
if ($TimeSeriesPath -ne '') {
    $full = [System.IO.Path]::GetFullPath($TimeSeriesPath)
    $dir = [System.IO.Path]::GetDirectoryName($full)
    if ($dir -and -not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }
    $writer = New-Object System.IO.StreamWriter($full, $false, (New-Object System.Text.UTF8Encoding($false)))
    # AutoFlush: a long soak writes one row per interval for hours; without this
    # the file stays empty until the run ends, so an interrupted soak (machine
    # sleep, killed job) loses every sample. Flushing per row costs nothing at
    # a 60s cadence and keeps the evidence on disk as it accrues.
    $writer.AutoFlush = $true
    $writer.WriteLine('timestamp,pid,cpuPercent,workingSetMb,privateMb,handles')
    Write-Output "TIMESERIES $full"
}

$baselineCpu = $proc.TotalProcessorTime.TotalSeconds
$baselineWall = [Diagnostics.Stopwatch]::StartNew()
$firstWorkingSet = $proc.WorkingSet64
$firstPrivate = $proc.PrivateMemorySize64
$firstHandles = $proc.HandleCount
$peakWorkingSet = $firstWorkingSet
$peakPrivate = $firstPrivate
$peakHandles = $firstHandles
$lastWorkingSet = $firstWorkingSet
$lastPrivate = $firstPrivate
$lastHandles = $firstHandles
$written = 1

try {
    for ($i = 1; $i -lt $samples; $i++) {
        Start-Sleep -Seconds $interval

        $current = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
        if (-not $current) {
            Write-Output "ERROR process $($proc.Id) exited before sampling finished (after $i of $samples samples)"
            exit 3
        }

        $peakWorkingSet = [Math]::Max($peakWorkingSet, $current.WorkingSet64)
        $peakPrivate = [Math]::Max($peakPrivate, $current.PrivateMemorySize64)
        $peakHandles = [Math]::Max($peakHandles, $current.HandleCount)
        $lastWorkingSet = $current.WorkingSet64
        $lastPrivate = $current.PrivateMemorySize64
        $lastHandles = $current.HandleCount

        if ($writer) {
            $elapsed = $baselineWall.Elapsed.TotalSeconds
            if ($elapsed -le 0) { $elapsed = 0.001 }
            $cpuNow = (($current.TotalProcessorTime.TotalSeconds - $baselineCpu) / $elapsed) / $logicalCores * 100.0
            $wsMb = [Math]::Round($current.WorkingSet64 / 1MB, 2)
            $pvMb = [Math]::Round($current.PrivateMemorySize64 / 1MB, 2)
            $stamp = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ss')
            $writer.WriteLine("$stamp,$($proc.Id),$([Math]::Round($cpuNow,3)),$wsMb,$pvMb,$($current.HandleCount)")
            $written++
        }
    }
}
finally {
    if ($writer) { $writer.Flush(); $writer.Dispose() }
}

$cpuSeconds = $proc.TotalProcessorTime.TotalSeconds - $baselineCpu
$wallSeconds = $baselineWall.Elapsed.TotalSeconds
if ($wallSeconds -le 0) {
    Write-Output 'ERROR zero elapsed wall time'
    exit 4
}

$cpuPercent = ($cpuSeconds / $wallSeconds) / $logicalCores * 100.0
$workingSetMb = [Math]::Round($peakWorkingSet / 1MB, 1)
$privateMb = [Math]::Round($peakPrivate / 1MB, 1)
$firstWsMb = [Math]::Round($firstWorkingSet / 1MB, 2)
$lastWsMb = [Math]::Round($lastWorkingSet / 1MB, 2)

if ($firstWorkingSet -gt 0) {
    $growthPercent = ($lastWorkingSet - $firstWorkingSet) / $firstWorkingSet * 100.0
} else {
    $growthPercent = 0.0
}

Write-Output "SAMPLES $written"
Write-Output ("CPU-PERCENT {0:N2}" -f $cpuPercent)
Write-Output "PEAK-WORKINGSET-MB $workingSetMb"
Write-Output "PEAK-PRIVATE-MB $privateMb"
Write-Output "FIRST-WORKINGSET-MB $firstWsMb"
Write-Output "LAST-WORKINGSET-MB $lastWsMb"
Write-Output ("MEM-GROWTH-PERCENT {0:N2}" -f $growthPercent)
Write-Output "PEAK-HANDLES $peakHandles"
Write-Output "HND-GROWTH $($lastHandles - $firstHandles)"

$cpuOk = $cpuPercent -le $CpuBudgetPercent
$memOk = $workingSetMb -le $MemoryBudgetMb
$leakOk = $growthPercent -le $LeakBudgetPercent

if ($cpuOk -and $memOk -and $leakOk) {
    Write-Output 'BENCH-PASS'
    exit 0
}

if (-not $cpuOk) { Write-Output ("FAIL-CPU {0:N2}% > {1}%" -f $cpuPercent, $CpuBudgetPercent) }
if (-not $memOk) { Write-Output ("FAIL-MEM {0}MB > {1}MB" -f $workingSetMb, $MemoryBudgetMb) }
if (-not $leakOk) { Write-Output ("FAIL-LEAK {0:N2}% > {1}%" -f $growthPercent, $LeakBudgetPercent) }
Write-Output 'BENCH-FAIL'
exit 1
