<#
    tools/bench/run-power.ps1 —— 常驻功耗基准（docs/12 §4 / docs/41 D-14）

    门禁：单宠物空闲 8 小时 CPU ≤ 0.5%（单核）、内存 ≤ 150MB。
    本脚本只负责「采得到数并给出判定」，不保证跑满 8 小时：
    要 8 小时就 -DurationSeconds 28800。默认 120 秒用于每次改动后的快速回归。

    用法：
        dotnet run --project packages/core/PetDock.Core -c Release &
        pwsh tools/bench/run-power.ps1                      # 采样已运行的实例
        pwsh tools/bench/run-power.ps1 -DurationSeconds 60  # 快速回归

    输出形如：
        SAMPLES 12
        CPU-PERCENT 0.21
        PEAK-WORKINGSET-MB 38.4
        PEAK-PRIVATE-MB 40.1
        BENCH-PASS

    实现注意（账本踩坑）：
      - 只用 cmdlet（Get-Process / Measure-Object），不把本机程序的输出过 Select-Object -First，
        也不把本机程序的输出过 Where-Object —— 两者都会吞错误行并误报退出码。
      - 采样间隔固定 5 秒，CPU 用 TotalProcessorTime 增量对墙上时间增量之比再除以逻辑核数，
        得到「相对单核」的百分比，与 docs/41 的口径一致。
#>
[CmdletBinding()]
param(
    [string]$ProcessName = 'PetDock.Core',
    [int]$DurationSeconds = 120,
    [double]$CpuBudgetPercent = 0.5,
    [int]$MemoryBudgetMb = 150,
    [int]$IntervalSeconds = 5
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$interval = [Math]::Max(1, $IntervalSeconds)
$samples = [Math]::Max(2, [int][Math]::Floor($DurationSeconds / $interval))

$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) {
    Write-Output "ERROR no running process named '$ProcessName' — start PetDock first"
    exit 2
}

$logicalCores = [Environment]::ProcessorCount
Write-Output "PROCESS $($proc.Id) cores=$logicalCores interval=${interval}s samples=$samples budget-cpu=$CpuBudgetPercent% budget-mem=${MemoryBudgetMb}MB"

$baselineCpu = $proc.TotalProcessorTime.TotalSeconds
$baselineWall = [Diagnostics.Stopwatch]::StartNew()
$peakWorkingSet = $proc.WorkingSet64
$peakPrivate = $proc.PrivateMemorySize64

for ($i = 1; $i -lt $samples; $i++) {
    Start-Sleep -Seconds $interval

    $current = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
    if (-not $current) {
        Write-Output "ERROR process $($proc.Id) exited before sampling finished"
        exit 3
    }

    $peakWorkingSet = [Math]::Max($peakWorkingSet, $current.WorkingSet64)
    $peakPrivate = [Math]::Max($peakPrivate, $current.PrivateMemorySize64)
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

Write-Output "SAMPLES $samples"
Write-Output ("CPU-PERCENT {0:N2}" -f $cpuPercent)
Write-Output "PEAK-WORKINGSET-MB $workingSetMb"
Write-Output "PEAK-PRIVATE-MB $privateMb"

$cpuOk = $cpuPercent -le $CpuBudgetPercent
$memOk = $workingSetMb -le $MemoryBudgetMb
if ($cpuOk -and $memOk) {
    Write-Output 'BENCH-PASS'
    exit 0
}

Write-Output 'BENCH-FAIL'
exit 1
