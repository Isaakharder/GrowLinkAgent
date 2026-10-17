# The 1.1.0 installer run by a standard (non-administrator) Windows account.
# The CI machine's own account is an administrator, so this creates a temporary local
# standard user, runs setup as that user (no elevation is possible for it), checks where
# it installed, and removes the account.
param([Parameter(Mandatory)] [string] $New, [Parameter(Mandatory)] [string] $Out)
$ErrorActionPreference = 'Stop'
$New = (Resolve-Path $New).Path
New-Item -ItemType Directory -Force $Out | Out-Null
$user = 'agentstd' + (Get-Random -Maximum 9999)
$pass = 'Std-' + [guid]::NewGuid().ToString('N').Substring(0, 16) + '!a1'
$secure = ConvertTo-SecureString $pass -AsPlainText -Force
New-LocalUser -Name $user -Password $secure -AccountNeverExpires -PasswordNeverExpires | Out-Null
try {
    $isAdmin = (Get-LocalGroupMember -Group Administrators | Where-Object { $_.Name -like "*\$user" }).Count -gt 0
    Write-Host "account $user is administrator: $isAdmin"
    $cred = New-Object System.Management.Automation.PSCredential (".\$user", $secure)
    $log = "C:\Users\Public\std-user-setup.log"
    Copy-Item $New C:\Users\Public\GrowLinkAgent-Setup.exe -Force
    # Run setup through a scheduled task registered for that account: Windows then signs the
    # account in properly (its own profile, AppData and TEMP), as when the user runs setup.
    # (Start-Process -Credential from this service account doesn't give access to its AppData.)
    $action = New-ScheduledTaskAction -Execute 'C:\Users\Public\GrowLinkAgent-Setup.exe' `
        -Argument "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS=startup `"/LOG=$log`""
    # A scheduled task with a stored password needs "Log on as a batch job" for that account
    # (a right only; the account stays a standard user without administrator rights).
    $sid = (Get-LocalUser $user).SID.Value
    secedit /export /cfg C:\Users\Public\secpol.inf /areas USER_RIGHTS | Out-Null
    $inf = Get-Content C:\Users\Public\secpol.inf
    $inf = $inf -replace '^(SeBatchLogonRight = .*)$', "`$1,*$sid"
    if (-not ($inf -match '^SeBatchLogonRight')) { $inf += "SeBatchLogonRight = *$sid" }
    $inf | Set-Content C:\Users\Public\secpol-new.inf -Encoding Unicode
    secedit /configure /db C:\Users\Public\secpol.sdb /cfg C:\Users\Public\secpol-new.inf /areas USER_RIGHTS | Out-Null
    $task = "GrowLinkAgentStdUserTest-$user"
    Register-ScheduledTask -TaskName $task -Action $action -User $user -Password $pass -RunLevel Limited `
        -Settings (New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 10)) | Out-Null
    Start-ScheduledTask -TaskName $task
    Start-Sleep 3
    $deadline = (Get-Date).AddMinutes(10)
    while ((Get-ScheduledTask -TaskName $task).State -eq 'Running' -and (Get-Date) -lt $deadline) { Start-Sleep 2 }
    $setupExit = (Get-ScheduledTaskInfo -TaskName $task).LastTaskResult
    Unregister-ScheduledTask -TaskName $task -Confirm:$false
    Get-Process GrowLinkAgent -ErrorAction SilentlyContinue | Stop-Process -Force
    $profileDir = (Get-CimInstance Win32_UserProfile | Where-Object { $_.LocalPath -like "*\$user*" } | Select-Object -First 1).LocalPath
    if (-not $profileDir) { $profileDir = "C:\Users\$user" }
    $exe = "$profileDir\AppData\Local\Programs\GrowLinkAgent\GrowLinkAgent.exe"
    $text = if (Test-Path $log) { Get-Content $log -Raw } else { '' }
    $lines = @(
        "standard user: $user (administrator: $isAdmin)",
        "setup exit code (scheduled task result): $setupExit",
        "installed for that user in AppData\Local\Programs: $(Test-Path $exe)",
        "setup log says Administrative install mode: No → $($text -match 'Administrative install mode: No')",
        "setup log says User privileges: $([regex]::Match($text, 'User privileges: (\w+)').Groups[1].Value)",
        "Startup shortcut for that user: $(Test-Path "$profileDir\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\GrowLinkAgent.lnk")",
        "profile: $profileDir")
    $lines | Set-Content "$Out/standard-user-install.txt"; $lines | ForEach-Object { Write-Host $_ }
    if (Test-Path $log) { Copy-Item $log "$Out/standard-user-setup.log" }
    if ($isAdmin -or -not (Test-Path $exe) -or $text -notmatch 'Administrative install mode: No') { throw 'standard-user install did not succeed as expected' }
    Write-Host 'STANDARD USER INSTALL PASSED'
} finally {
    Get-CimInstance Win32_UserProfile | Where-Object { $_.LocalPath -like "*\$user" } | Remove-CimInstance -ErrorAction SilentlyContinue
    Remove-LocalUser -Name $user -ErrorAction SilentlyContinue
}
