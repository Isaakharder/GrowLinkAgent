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
    $p = Start-Process C:\Users\Public\GrowLinkAgent-Setup.exe -Credential $cred -LoadUserProfile -WorkingDirectory C:\Users\Public `
        -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$log`"", '/TASKS=startup' -PassThru
    # Wait for setup only (not for the agent it starts at the end).
    $null = $p.WaitForExit(600000)
    Get-Process GrowLinkAgent -ErrorAction SilentlyContinue | Stop-Process -Force
    $exe = "C:\Users\$user\AppData\Local\Programs\GrowLinkAgent\GrowLinkAgent.exe"
    $text = if (Test-Path $log) { Get-Content $log -Raw } else { '' }
    $lines = @(
        "standard user: $user (administrator: $isAdmin)",
        "setup exit code: $($p.ExitCode)",
        "installed for that user in AppData\Local\Programs: $(Test-Path $exe)",
        "setup log says Administrative install mode: No → $($text -match 'Administrative install mode: No')",
        "setup log says User privileges: $([regex]::Match($text, 'User privileges: (\w+)').Groups[1].Value)",
        "Startup shortcut for that user: $(Test-Path "C:\Users\$user\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\GrowLinkAgent.lnk")")
    $lines | Set-Content "$Out/standard-user-install.txt"; $lines | ForEach-Object { Write-Host $_ }
    if (Test-Path $log) { Copy-Item $log "$Out/standard-user-setup.log" }
    if ($isAdmin -or $p.ExitCode -ne 0 -or -not (Test-Path $exe)) { throw 'standard-user install did not succeed as expected' }
    Write-Host 'STANDARD USER INSTALL PASSED'
} finally {
    Get-CimInstance Win32_UserProfile | Where-Object { $_.LocalPath -like "*\$user" } | Remove-CimInstance -ErrorAction SilentlyContinue
    Remove-LocalUser -Name $user -ErrorAction SilentlyContinue
}
