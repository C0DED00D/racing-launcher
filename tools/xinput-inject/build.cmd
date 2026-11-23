@echo off
rem Builds the stand-in xinput1_4.dll for x64 (x64\) and x86 (x86\) with VS Build Tools 2026.
setlocal
set VSDEV="C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\Common7\Tools\VsDevCmd.bat"
cd /d "%~dp0"
for %%A in (x64 x86) do (
  call :build %%A || exit /b 1
)
exit /b 0

:build
setlocal
if "%1"=="x64" (call %VSDEV% -arch=amd64 -host_arch=amd64 >nul) else (call %VSDEV% -arch=x86 -host_arch=amd64 >nul)
if errorlevel 1 exit /b 1
cd /d "%~dp0%1"
cl /nologo /O2 /LD /W4 ..\xinput_inject.c /link /DEF:..\xinput_inject.def /OUT:xinput1_4.dll user32.lib dinput8.lib dxguid.lib || exit /b 1
endlocal
exit /b 0
