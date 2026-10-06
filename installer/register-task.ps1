param([string]$Exe)
# Registra una tarea programada propia con máximos privilegios, sin disparador:
# solo se lanza a demanda (schtasks /Run). Es el mecanismo que permite que el
# monitor arranque siempre como administrador sin volver a pedir UAC.
$ErrorActionPreference = 'SilentlyContinue'
if (-not $Exe) { exit 1 }
$a  = New-ScheduledTaskAction -Execute $Exe -Argument '--via-task'
$s  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
       -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName 'MonitorRedPCJ' -Action $a -Settings $s -RunLevel Highest -Force | Out-Null
exit 0
