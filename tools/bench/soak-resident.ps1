<#
    tools/bench/soak-resident.ps1 -- stage the pet, launch it, and soak it in ONE process tree.

    Why each part exists (both reasons paid for in this project's ledger):

      (1) ONE process tree. Launched separately, the pet and the sampler ended up in
          different tool-call process trees; killing the sampler job tore down the
          tree and killed the pet with it. So this script starts the pet, samples it
          and reports, all inside the process the caller supervises.

      (2) ISOLATION FROM BUILDS. The running pet keeps the plugin assembly it loaded
          open (Assembly.LoadFrom holds the file), so a teammate's `dotnet build`
          fails with MSB3027/MSB3021, and the teammate's workaround is
          `Stop-Process PetDock.Core` -- which silently ends the soak with no crash
          dump, no event-log entry and an empty stderr. Measured 2026-10-16: the pet
          vanished 812s into an 8h soak, exactly as packages/plugins/**/bin was
          rewritten. So by default this script stages a mirror of
          packages/*/*/bin/<Config>/<tfm> under artifacts/soak/repo/ (gitignored) and
          launches the STAGED copy. The kernel finds its plugin by walking up from its
          own directory to the first `packages/plugins`, so the staged tree is
          self-contained and the pet never opens a file under the real packages/.

    Usage:
        & .\tools\bench\soak-resident.ps1 -DurationSeconds 28800
        & .\tools\bench\soak-resident.ps1 -DurationSeconds 60 -IntervalSeconds 5
        & .\tools\bench\soak-resident.ps1 -ExePath <path> -NoStage    # raw, no staging

    Output: everything run-power.ps1 prints, plus:
        PET-STAGED mirror=<path> files=<n> plugin-dlls=<n>
        PET-STARTED pid=<n> exe=<path>
        PET-EXITED-EARLY at <timestamp>              (only if it dies mid-soak)
        SOAK-DONE

    Exit codes: same as run-power.ps1, plus 5 = pet never came up.

    ASCII only: executed by powershell 5.1, which reads a BOM-less .ps1 as ANSI;
    non-ASCII comments would be mangled into syntax errors.
#>
[CmdletBinding()]
param(
    [string]$ExePath = '',
    [int]$DurationSeconds = 28800,
    [int]$IntervalSeconds = 60,
    [string]$TimeSeriesPath = '.dsh-code\soak-samples.csv',
    [string]$PidFile = '.dsh-code\soak-pid.txt',
    [int]$SettleSeconds = 5,
    [string]$Configuration = 'Release',
    [string]$StageRoot = 'artifacts\soak',
    [switch]$NoStage
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

function Copy-Output {
    param([string]$Source, [string]$Destination)
    if (Test-Path -LiteralPath $Destination) {
        Remove-Item -LiteralPath $Destination -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Copy-Item -Path (Join-Path $Source '*') -Destination $Destination -Recurse -Force
    return (Get-ChildItem -LiteralPath $Destination -File -Recurse | Measure-Object).Count
}

if (-not $NoStage -and $ExePath -eq '') {
    $mirrorRepo = [System.IO.Path]::GetFullPath((Join-Path $repo $StageRoot)) | Join-Path -ChildPath 'repo'
    $copied = 0
    # packages/<layer>/<project>/bin (core, sdk) and packages/plugins/<pack>/<project>/bin.
    # Both depths are needed: a single-depth glob silently stages zero plugin assemblies,
    # and then the kernel walks past the mirror and locks the real packages/ copy.
    $binDirs = @()
    foreach ($pattern in @('packages\*\*\bin', 'packages\*\*\*\bin')) {
        $binDirs = $binDirs + @(Get-ChildItem -Path (Join-Path $repo $pattern) -Directory -ErrorAction SilentlyContinue)
    }
    foreach ($bin in $binDirs) {
        $configurationDir = Join-Path $bin.FullName $Configuration
        if (-not (Test-Path -LiteralPath $configurationDir)) { continue }
        foreach ($tfm in (Get-ChildItem -LiteralPath $configurationDir -Directory -ErrorAction SilentlyContinue)) {
            $relative = $tfm.FullName.Substring($repo.Length).TrimStart('\')
            $destination = Join-Path $mirrorRepo $relative
            $copied = $copied + (Copy-Output -Source $tfm.FullName -Destination $destination)
        }
    }
    $pluginDlls = @(Get-ChildItem -Path (Join-Path $mirrorRepo 'packages\plugins') -Recurse -Filter '*.dll' -ErrorAction SilentlyContinue)
    $ExePath = Join-Path $mirrorRepo ('packages\core\PetDock.Core\bin\' + $Configuration + '\net8.0-windows\PetDock.Core.exe')
    Write-Output "PET-STAGED mirror=$mirrorRepo files=$copied plugin-dlls=$($pluginDlls.Count)"
    if ($pluginDlls.Count -eq 0) {
        Write-Output 'ERROR staging produced no plugin assemblies under mirror/packages/plugins.'
        Write-Output 'The pet would walk past the mirror and lock the real packages/ copy, which is exactly'
        Write-Output 'what this staging exists to prevent. Refusing to start a soak that measures that.'
        Write-Output 'Use -NoStage only if you deliberately want the raw in-repo executable.'
        exit 5
    }
}

if ($ExePath -eq '') {
    $ExePath = Join-Path $repo ('packages\core\PetDock.Core\bin\' + $Configuration + '\net8.0-windows\PetDock.Core.exe')
}
if (-not (Test-Path -LiteralPath $ExePath)) {
    Write-Output "ERROR exe not found: $ExePath"
    exit 5
}

$pidFull = [System.IO.Path]::GetFullPath((Join-Path $repo $PidFile))
$pidDir = [System.IO.Path]::GetDirectoryName($pidFull)
if ($pidDir -and -not (Test-Path -LiteralPath $pidDir)) {
    New-Item -ItemType Directory -Force -Path $pidDir | Out-Null
}

$petOut = [System.IO.Path]::GetFullPath((Join-Path $repo '.dsh-code\soak-pet.out'))
$petErr = [System.IO.Path]::GetFullPath((Join-Path $repo '.dsh-code\soak-pet.err'))
$pet = Start-Process -FilePath $ExePath -PassThru -RedirectStandardOutput $petOut -RedirectStandardError $petErr
Write-Output "PET-STARTED pid=$($pet.Id) exe=$ExePath"
Set-Content -LiteralPath $pidFull -Value $pet.Id -Encoding ASCII
# Record the start instant HERE, not from $pet.StartTime further down.
# Start-Process -PassThru hands back a Process object whose StartTime can come
# back null (no handle was opened by us), and `(Get-Date) - $null` throws
# "Cannot find an overload for op_Subtraction and the argument count: 2" --
# which is exactly how the 2026-10-24 soak died after its first sample, far
# from here, looking like a sampler bug instead of a measurement bug.
$startedAt = Get-Date

# Give the window time to appear and draw its first frame before the sampling
# baseline is taken; otherwise the first sample measures startup, not residency.
Start-Sleep -Seconds $SettleSeconds

if ($pet.HasExited) {
    Write-Output "ERROR pet exited during startup (exit=$($pet.ExitCode))"
    exit 5
}

$tsFull = [System.IO.Path]::GetFullPath((Join-Path $repo $TimeSeriesPath))
& (Join-Path $PSScriptRoot 'run-power.ps1') `
    -ProcessId $pet.Id `
    -DurationSeconds $DurationSeconds `
    -IntervalSeconds $IntervalSeconds `
    -TimeSeriesPath $tsFull
$benchExit = $LASTEXITCODE

if ($benchExit -eq 3) {
    Write-Output "PET-EXITED-EARLY at $((Get-Date).ToString('yyyy-MM-ddTHH:mm:ss'))"
}

$still = Get-Process -Id $pet.Id -ErrorAction SilentlyContinue

# The sampler counts samples, not wall-clock, so the pet's actual residency can
# drift a few seconds away from -DurationSeconds. Print it and say so out loud:
# "resident for 8 hours" must be a measured number, not an inference from the
# harness having printed PET-STARTED.
$residency = ((Get-Date) - $startedAt).TotalSeconds
Write-Output ("PET-RESIDENCY-SECONDS " + [Math]::Round($residency, 1))
Write-Output ("PET-RESIDENCY-HOURS " + [Math]::Round($residency / 3600.0, 3))
if ($residency -lt $DurationSeconds) {
    Write-Output ("WARN residency-short by " + [Math]::Round($DurationSeconds - $residency, 1) + " s")
}

if ($still) {
    Write-Output "PET-STILL-RUNNING pid=$($pet.Id) - stopping it now that the soak is over"
    $still | Stop-Process -Force -ErrorAction SilentlyContinue
} else {
    Write-Output "PET-NOT-RUNNING at soak end"
}

Write-Output 'SOAK-DONE'
exit $benchExit
