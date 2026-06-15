@echo off
REM ========================================================================
REM UO Offline — Windows installer launcher
REM Double-click this, or run it from a command prompt. It invokes the
REM PowerShell installer with the execution policy bypassed (Windows blocks
REM unsigned .ps1 scripts by default), so you don't have to change any
REM system settings.
REM ========================================================================
echo.
echo  UO Offline - Windows Installer
echo  This will install ModernUO, ClassicUO, the bots, and the game data.
echo  It can take 15-25 minutes. Keep this window open.
echo.
pause
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
echo.
echo  Done. If there were no errors above, double-click the "UO Offline"
echo  shortcut on your Desktop to play.
echo.
pause
