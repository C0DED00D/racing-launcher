@echo off
rem Builds the x64 version.dll stand-in for Supermodel's Linked seats (output: x64\version.dll + version_orig.dll,
rem a copy of System32\version.dll that it forwards to).
rem Needs Visual Studio or its Build Tools with "Desktop development with C++" (found by vswhere; or set VSDEV to
rem the full path of VsDevCmd.bat).
setlocal
if not defined VSDEV for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSDEV=%%i\Common7\Tools\VsDevCmd.bat"
if not exist "%VSDEV%" (echo Visual Studio C++ build tools not found: install "Desktop development with C++" or set VSDEV. & exit /b 1)
call "%VSDEV%" -arch=amd64 -host_arch=amd64 >nul || exit /b 1
cd /d "%~dp0"
if not exist x64 mkdir x64
cd x64
cl /nologo /O2 /LD /W3 /D_CRT_SECURE_NO_WARNINGS ..\dinput_bg.c /link /DEF:..\dinput_bg.def /OUT:version.dll user32.lib || exit /b 1
copy /y %SystemRoot%\System32\version.dll version_orig.dll >nul
