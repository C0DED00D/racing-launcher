@echo off
rem Builds the x64 version.dll stand-in for Supermodel's Linked seats (output: x64\version.dll + version_orig.dll,
rem a copy of System32\version.dll that it forwards to) with VS Build Tools 2026.
setlocal
call "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\Common7\Tools\VsDevCmd.bat" -arch=amd64 -host_arch=amd64 >nul || exit /b 1
cd /d "%~dp0"
if not exist x64 mkdir x64
cd x64
cl /nologo /O2 /LD /W3 /D_CRT_SECURE_NO_WARNINGS ..\dinput_bg.c /link /DEF:..\dinput_bg.def /OUT:version.dll user32.lib || exit /b 1
copy /y %SystemRoot%\System32\version.dll version_orig.dll >nul