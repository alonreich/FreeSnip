@echo off
rem ---------------------------------------------------------------------------
rem  dev.cmd — Developer local build launcher (parity alias for dev_build.cmd)
rem ---------------------------------------------------------------------------
set "SCRIPT_DIR=%~dp0"
pushd "%SCRIPT_DIR%" >nul || exit /b 1
call "%SCRIPT_DIR%dev_build.cmd" %*
set "EXIT_CODE=%ERRORLEVEL%"
popd >nul
exit /b %EXIT_CODE%
