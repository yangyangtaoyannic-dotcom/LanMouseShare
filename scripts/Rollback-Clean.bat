@echo off
setlocal EnableExtensions

net session >nul 2>&1
if not "%errorlevel%"=="0" (
  echo Requesting Administrator permission...
  powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)

echo [LanMouseShare rollback] Stopping Agent processes...
taskkill /IM LanMouseShare.Agent.exe /F >nul 2>&1

echo [LanMouseShare rollback] Stopping LanMouseShare service...
net stop LanMouseShare >nul 2>&1

echo [LanMouseShare rollback] Deleting LanMouseShare service...
sc.exe delete LanMouseShare >nul 2>&1

echo [LanMouseShare rollback] Removing administrator startup scheduled task...
schtasks.exe /Delete /TN "LanMouseShare.Agent.Admin" /F >nul 2>&1

echo [LanMouseShare rollback] Removing current-user startup registry entry...
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v "LanMouseShare.Agent" /f >nul 2>&1

echo [LanMouseShare rollback] Removing installed-version registry entry...
reg delete "HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\LanMouseShare" /f >nul 2>&1

for /f %%i in ('powershell.exe -NoProfile -Command "Get-Date -Format yyyyMMdd-HHmmss"') do set "STAMP=%%i"

set "INSTALL_DIR=%ProgramFiles%\LanMouseShare"
set "INSTALL_BACKUP=%ProgramFiles%\LanMouseShare.rollback-backup-%STAMP%"
if exist "%INSTALL_DIR%\" (
  echo [LanMouseShare rollback] Moving installed files to:
  echo   %INSTALL_BACKUP%
  move "%INSTALL_DIR%" "%INSTALL_BACKUP%" >nul 2>&1
  if not "%errorlevel%"=="0" (
    echo [LanMouseShare rollback] Could not move installed files. Please close all LanMouseShare windows and run again.
  )
) else (
  echo [LanMouseShare rollback] No installed files found in Program Files.
)

set "CONFIG_DIR=%ProgramData%\LanMouseShare"
set "CONFIG_FILE=%CONFIG_DIR%\config.json"
set "CONFIG_BACKUP=%CONFIG_DIR%\config-backup-before-rollback-%STAMP%.json"
if exist "%CONFIG_FILE%" (
  echo [LanMouseShare rollback] Backing up config to:
  echo   %CONFIG_BACKUP%
  move "%CONFIG_FILE%" "%CONFIG_BACKUP%" >nul 2>&1
) else (
  echo [LanMouseShare rollback] No config.json found.
)

echo.
echo [LanMouseShare rollback] Cleanup complete.
echo Now run the older LanMouseShare installer from its own dist folder.
echo Do not mix an old Agent with a new Service.
echo.
pause
