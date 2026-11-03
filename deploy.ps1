<#
.SYNOPSIS
  Compileaza si (re)instaleaza iControl. Idempotent, nu cere drepturi de administrator.

.DESCRIPTION
  1. Opreste curat instanta care ruleaza (iControl.exe --exit), ca ventilatorul sa treaca prin fail-safe.
  2. Compileaza bin\iControl.exe (build.cmd).
  3. Prima data: dezactiveaza vechile task-uri "Ventilator iMac" / "Luminozitate iMac",
     opreste FanControl.exe / BrightnessKeys.exe si preia nivelul de luminozitate salvat.
  4. Inregistreaza task-ul de logon "iControl" (utilizatorul curent, fara elevare) si il porneste.

.PARAMETER Uninstall
  Opreste aplicatia si sterge task-ul "iControl". Ventilatorul revine pe Automat SMC.
#>
param([switch]$Uninstall)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$exe = Join-Path $root 'bin\iControl.exe'
$taskName = 'iControl'
$dataDir = Join-Path $env:LOCALAPPDATA 'iControl'
$user = "$env:USERDOMAIN\$env:USERNAME"

function Stop-IControl {
    $proc = Get-Process iControl -ErrorAction SilentlyContinue
    if (-not $proc) { return }
    if (Test-Path $exe) { Start-Process -FilePath $exe -ArgumentList '--exit' -Wait }
    $proc | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
    $left = Get-Process iControl -ErrorAction SilentlyContinue
    if ($left) {
        Write-Warning 'iControl nu s-a inchis in 10 s; il opresc fortat.'
        $left | Stop-Process -Force
    }
    Write-Host 'iControl oprit.'
}

Stop-IControl

if ($Uninstall) {
    if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
        Write-Host "Task-ul '$taskName' a fost sters."
    }
    return
}

& cmd /c "`"$root\build.cmd`""
if ($LASTEXITCODE -ne 0) { throw "Compilarea a esuat (cod $LASTEXITCODE)." }
Write-Host "Compilat: $exe"

# Migrare de la aplicatiile separate (doar prima data are efect).
foreach ($old in 'Ventilator iMac', 'Luminozitate iMac') {
    $t = Get-ScheduledTask -TaskName $old -ErrorAction SilentlyContinue
    if ($t -and $t.State -ne 'Disabled') {
        Stop-ScheduledTask -TaskName $old -ErrorAction SilentlyContinue
        Disable-ScheduledTask -TaskName $old | Out-Null
        Write-Host "Task vechi dezactivat: $old"
    }
}
Get-Process FanControl, BrightnessKeys -ErrorAction SilentlyContinue | Stop-Process -Force

New-Item -ItemType Directory -Force $dataDir | Out-Null
$oldLevel = Join-Path $env:LOCALAPPDATA 'iMacBrightnessKeys\level.txt'
$newLevel = Join-Path $dataDir 'level.txt'
if ((Test-Path $oldLevel) -and -not (Test-Path $newLevel)) {
    Copy-Item $oldLevel $newLevel
    Write-Host 'Nivelul de luminozitate salvat a fost preluat.'
}

$action = New-ScheduledTaskAction -Execute $exe -WorkingDirectory (Split-Path $exe)
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Settings $settings `
    -Principal $principal -Description 'iControl: ventilator SMC + afisaj taskbar + taste F1/F3/F4/F11/F12 (iMac Boot Camp)' `
    -Force | Out-Null
Write-Host "Task '$taskName' inregistrat."

Start-ScheduledTask -TaskName $taskName
Start-Sleep -Seconds 3
$p = Get-Process iControl -ErrorAction SilentlyContinue
if ($p) { Write-Host "iControl ruleaza (PID $($p.Id))." } else { Write-Warning 'iControl nu ruleaza; vezi jurnalul.' }
Get-Content (Join-Path $dataDir 'log.txt') -Tail 8 -ErrorAction SilentlyContinue
