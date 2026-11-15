param(
    [Parameter(Mandatory = $true)]
    [string] $ExecutablePath,

    [string] $TaskName = "ServerPulse"
)

$ErrorActionPreference = "Stop"
$exe = (Resolve-Path -LiteralPath $ExecutablePath).Path
$workingDirectory = Split-Path -Parent $exe
$user = "$env:USERDOMAIN\$env:USERNAME"

$action = New-ScheduledTaskAction -Execute $exe -Argument "--headless" -WorkingDirectory $workingDirectory
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal `
    -Description "Start ServerPulse API and collector when this user signs in." -Force | Out-Null

Write-Host "Scheduled task '$TaskName' registered for $user."
