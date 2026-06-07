param(
    [switch]$KeepConfig,
    [switch]$KeepStartup,
    [switch]$KeepInstalledFiles
)

$ErrorActionPreference = "Continue"

function Write-Step($message) {
    Write-Host "[LanMouseShare rollback] $message"
}

function Test-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Admin)) {
    Write-Host "This cleanup must run as Administrator. Restarting elevated..."
    $argsList = "-ExecutionPolicy Bypass -File `"$PSCommandPath`""
    if ($KeepConfig) { $argsList += " -KeepConfig" }
    if ($KeepStartup) { $argsList += " -KeepStartup" }
    if ($KeepInstalledFiles) { $argsList += " -KeepInstalledFiles" }
    Start-Process -FilePath "powershell.exe" -ArgumentList $argsList -Verb RunAs
    exit
}

Write-Step "Stopping Agent processes."
Get-Process -Name "LanMouseShare.Agent" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

Write-Step "Stopping LanMouseShare service."
& net.exe stop LanMouseShare 2>$null | Out-Null

Write-Step "Deleting LanMouseShare service if it exists."
& sc.exe delete LanMouseShare 2>$null | Out-Null

if (-not $KeepStartup) {
    Write-Step "Removing administrator startup scheduled task."
    & schtasks.exe /Delete /TN "LanMouseShare.Agent.Admin" /F 2>$null | Out-Null

    Write-Step "Removing current-user startup registry entry."
    $runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
    Remove-ItemProperty -Path $runKey -Name "LanMouseShare.Agent" -ErrorAction SilentlyContinue
}

if (-not $KeepInstalledFiles) {
    Write-Step "Removing installed-version registry entry."
    Remove-Item -LiteralPath "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\LanMouseShare" -Recurse -Force -ErrorAction SilentlyContinue

    $installDir = Join-Path $env:ProgramFiles "LanMouseShare"
    if (Test-Path -LiteralPath $installDir) {
        $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
        $backupDir = Join-Path $env:ProgramFiles "LanMouseShare.rollback-backup-$stamp"
        Write-Step "Moving installed files to $backupDir"
        try {
            Move-Item -LiteralPath $installDir -Destination $backupDir -Force
        }
        catch {
            Write-Step "Move failed, deleting installed files instead."
            Remove-Item -LiteralPath $installDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    else {
        Write-Step "No installed files found in Program Files."
    }
}

if (-not $KeepConfig) {
    $configDir = Join-Path $env:ProgramData "LanMouseShare"
    $configPath = Join-Path $configDir "config.json"
    if (Test-Path -LiteralPath $configPath) {
        $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
        $backupPath = Join-Path $configDir "config-backup-before-rollback-$stamp.json"
        Write-Step "Backing up config to $backupPath"
        Move-Item -LiteralPath $configPath -Destination $backupPath -Force
    }
    else {
        Write-Step "No config.json found."
    }
}

Write-Step "Cleanup complete. Now install or start the older LanMouseShare version you want to use."
Write-Host ""
Write-Host "Tip: install the old version from its own dist folder, and do not mix an old Agent with a new Service."
Write-Host "Press Enter to close."
[void][Console]::ReadLine()
