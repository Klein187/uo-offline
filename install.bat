@echo off
REM ========================================================================
REM UO Offline — Windows installer launcher
REM Double-click this. It opens the GUI installer with the execution policy
REM bypassed (Windows blocks unsigned .ps1 scripts by default), so you don't
REM have to change any system settings.
REM
REM Prefer a plain console install? Run:
REM   powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1
REM ========================================================================
start "" powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File "%~dp0install-gui.ps1"
