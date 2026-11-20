@echo off
rem ---------------------------------------------------------------------------------------------
rem  NoIntrovert ROM test matrix: runs noi against every game in a test folder, one at a time,
rem  with each preset, and records the result of every command for every game.
rem  What it makes goes to test-dest (beside test-src) as an ES-DE tree: roms\<system>\<preset>\.
rem  test-dest is emptied each time a run starts.
rem
rem    test-roms.cmd                                   test-src next to the repo, every default test
rem    test-roms.cmd -Source D:\test-src               another folder
rem    test-roms.cmd -Tests convert,verify-output      only some tests
rem    test-roms.cmd -Baseline test-results\20261119-101500\results.csv   show what got better or worse
rem    test-roms.cmd -Help                             every option
rem
rem  The work is done by test-roms.ps1 beside this file; this only starts it. PowerShell 7 (pwsh)
rem  is used when it's installed, Windows PowerShell 5.1 otherwise.
rem ---------------------------------------------------------------------------------------------
setlocal
set "PS=powershell.exe"
where pwsh.exe >nul 2>&1 && set "PS=pwsh.exe"

if /i "%~1"=="-Help" goto help
if /i "%~1"=="/?" goto help

"%PS%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0test-roms.ps1" %*
set "CODE=%ERRORLEVEL%"

rem Started by double-clicking (no arguments, own window): keep it open to read the summary.
if "%~1"=="" echo %CMDCMDLINE% | find /i "/c" >nul && pause
exit /b %CODE%

:help
"%PS%" -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "Get-Help '%~dp0test-roms.ps1' -Detailed"
exit /b 0
