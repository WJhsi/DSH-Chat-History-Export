@echo off
rem ============================================================
rem  DSH Chat-History Export - one-click build script (Win32 GUI exe)
rem  Usage: double-click build.cmd, or run from a cmd window.
rem  Output: dist\recover-session.exe
rem  Requires: .NET Framework 4.x (built into Windows 10/11)
rem ============================================================
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" goto nocsc

if not exist dist mkdir dist

"%CSC%" /nologo /target:winexe /optimize+ /unsafe /codepage:65001 /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /resource:native\libzstd.dll,libzstd.dll /out:dist\dsh-chat-history-export.exe src\dsh-chat-history-export-gui.cs
if errorlevel 1 goto fail

echo [OK] dist\dsh-chat-history-export.exe
exit /b 0

:nocsc
echo [ERROR] .NET Framework compiler not found: %CSC%
exit /b 1

:fail
echo [ERROR] Build failed. Fix src\dsh-chat-history-export-gui.cs and retry.
exit /b 1