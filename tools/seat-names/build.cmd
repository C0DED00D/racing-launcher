@echo off
rem Builds the 32-bit seat_names DLL twice (see seat_names.c):
rem   x86\xinput1_3.dll  for games that load xinput1_3 from their folder
rem   x86\version.dll    for games that load version.dll from their folder
rem Needs Visual Studio or its Build Tools with "Desktop development with C++" (found by vswhere; or set VSDEV to
rem the full path of VsDevCmd.bat).
setlocal
if not defined VSDEV for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSDEV=%%i\Common7\Tools\VsDevCmd.bat"
if not exist "%VSDEV%" (echo Visual Studio C++ build tools not found: install "Desktop development with C++" or set VSDEV. & exit /b 1)
call "%VSDEV%" -arch=x86 -host_arch=amd64 >nul || exit /b 1
cd /d "%~dp0" || exit /b 1
if not exist x86 mkdir x86
cl /nologo /O2 /W3 /LD /MT seat_names.c /Fo:x86\xinput.obj /Fe:x86\xinput1_3.dll /link /DEF:seat_names.def || exit /b 1
cl /nologo /O2 /W3 /LD /MT /DPROXY_VERSION seat_names.c /Fo:x86\version.obj /Fe:x86\version.dll /link /DEF:seat_names_version.def || exit /b 1
del x86\*.obj x86\*.exp x86\*.lib 2>nul
dir /b x86
