@echo off
rem Builds the stand-in xinput1_4.dll for x64 (x64\) and x86 (x86\).
rem Needs Visual Studio or its Build Tools with "Desktop development with C++" (found by vswhere; or set VSDEV to
rem the full path of VsDevCmd.bat).
setlocal
if not defined VSDEV for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSDEV=%%i\Common7\Tools\VsDevCmd.bat"
if not exist "%VSDEV%" (echo Visual Studio C++ build tools not found: install "Desktop development with C++" or set VSDEV. & exit /b 1)
cd /d "%~dp0"
for %%A in (x64 x86) do (
  call :build %%A || exit /b 1
)
exit /b 0

:build
setlocal
if not exist "%~dp0%1" mkdir "%~dp0%1"
if "%1"=="x64" (call "%VSDEV%" -arch=amd64 -host_arch=amd64 >nul) else (call "%VSDEV%" -arch=x86 -host_arch=amd64 >nul)
if errorlevel 1 exit /b 1
cd /d "%~dp0%1"
cl /nologo /O2 /LD /W4 ..\xinput_inject.c /link /DEF:..\xinput_inject.def /OUT:xinput1_4.dll user32.lib dinput8.lib dxguid.lib || exit /b 1
endlocal
exit /b 0
