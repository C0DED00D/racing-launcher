@echo off
rem Started at logon by "Racing Boot Select.lnk" in the user's Startup folder.
rem Smart App Control sometimes blocks our unsigned BootSelect.exe in the first minutes after boot and lets the
rem same file run a little later, so retry every 15 s for up to 3 minutes. BootSelect is a GUI exe, so cmd starts
rem it directly (CreateProcess) without waiting: errorlevel 0 = started, nonzero = Windows refused it. Do not use
rem `start` here: on failure it opens an error dialog and waits for a click.
setlocal
set "EXE=%~dp0..\src\BootSelect\bin\Debug\net10.0-windows\BootSelect.exe"
set "LOG=%~dp0..\runtime\logs\boot-select.log"
for /l %%i in (1,1,12) do (
    "%EXE%" %*
    if not errorlevel 1 exit /b 0
    call :note %%i
    timeout /t 15 /nobreak >nul
)
exit /b 1

:note
>>"%LOG%" echo %date% %time% boot-select.cmd: BootSelect.exe did not start (attempt %1/12, code %errorlevel%); retrying
exit /b 0
