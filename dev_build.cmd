@echo off
set "DOTNET_CLI_FORCE_UTF8_ENCODING=false"
set "DOTNET_CLI_UI_LANGUAGE=en-US"
set "VSCONSOLEOUTPUT=1"
set "SCRIPT_DIR=%~dp0"
pushd "%SCRIPT_DIR%" >nul || exit /b 1

rem ---------------------------------------------------------------------------
rem  dev_build.cmd — Local Development Build (Zero Git/GitHub Interaction)
rem  Compiles both branches to .\compiled\ locally. Does not touch git tags,
rem  does not publish to GitHub, and does not wipe pre-existing compiled binaries.
rem ---------------------------------------------------------------------------

if /I "%~1"=="__BUILD_LOGGED__" goto :run_logged

  powershell -NoProfile -ExecutionPolicy Bypass -File ".\developer_tools\BuildLog.ps1" -BatchPath ".\dev_build.cmd" %* <nul

  set "BUILD_EXIT=%ERRORLEVEL%"

  if "%BUILD_EXIT%"=="1" (
    echo.
    echo ###########################################################
    echo  LOCAL DEV BUILD FAILED.
    echo  Scroll up for the first ERROR line, or read .\build.log
    echo ###########################################################
    pause
  )

  popd >nul

  powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%developer_tools\SetConsoleFont.ps1" <nul

  exit /b %BUILD_EXIT%

:run_logged
shift

setlocal enabledelayedexpansion

cd /d "."

set "PROJECT_FILE=src\freesnip\freesnip.csproj"
set "PUBLISH_BASE_ARGS=-p:TreatWarningsAsErrors=true"
set "DOTNET_LOG_ARGS=-consoleLoggerParameters:Summary;NoItemAndPropertyList"
set "DO_PUBLISH=0"

rem ---------------------------------------------------------------------------
rem  VERSION STAMP: computed locally for binary assembly metadata
rem ---------------------------------------------------------------------------
set "BUILD_VERSION="
for /f "usebackq delims=" %%V in (`powershell -NoProfile -Command "Get-Date -Format yyyy.MM.dd.HHmm"`) do set "BUILD_VERSION=%%V"
if not defined BUILD_VERSION (
  echo ERROR: could not compute a build version.
  exit /b 1
)
set "TAG=v!BUILD_VERSION!"
set "VERSION_ARGS=-p:Version=!BUILD_VERSION! -p:FileVersion=!BUILD_VERSION! -p:InformationalVersion=!BUILD_VERSION!"
echo Local Dev Build version: !BUILD_VERSION!  ^(tag !TAG!^)

powershell -NoProfile -Command "Set-Content -Path '.\version.txt' -Value '!BUILD_VERSION!' -NoNewline; Set-Content -Path '.\Directory.Build.props' -Value ('<Project>`r`n  <PropertyGroup>`r`n    <Version>!BUILD_VERSION!</Version>`r`n    <AssemblyVersion>!BUILD_VERSION!</AssemblyVersion>`r`n    <FileVersion>!BUILD_VERSION!</FileVersion>`r`n    <InformationalVersion>!BUILD_VERSION!</InformationalVersion>`r`n    <ProductVersion>!BUILD_VERSION!</ProductVersion>`r`n  </PropertyGroup>`r`n</Project>')" <nul

echo ###########################################################
echo PREPARING LOCAL BUILD ENVIRONMENT...
echo ###########################################################

call :TERMINATE_PROCESSES

if exist ".\compiled" rd /s /q ".\compiled"
mkdir ".\compiled"

call :CLEAN_ALL

echo.
echo ###########################################################
echo RUNNING HEADLESS TEST SUITE...
echo ###########################################################

dotnet test "src\freesnip.tests\freesnip.tests.csproj" -c Release --nologo %DOTNET_LOG_ARGS%
if errorlevel 1 (
  echo ERROR: Headless test suite failed.
  exit /b 1
)

echo.
echo ###########################################################
echo BUILDING BRANCH 1: Native (Local Dev)
echo ###########################################################

where link.exe >nul 2>&1
if not errorlevel 1 (
  echo Native AOT toolchain detected ^(link.exe on PATH^).
) else (
  echo NOTE: link.exe not on PATH - the .NET SDK will locate the MSVC toolchain itself.
)

call :BUILD_STANDALONE "Branch1" "FreeSnip" "USE_TESSERACT=false" "-p:PublishAot=true" "1"
if errorlevel 1 exit /b 1

echo.
echo ###########################################################
echo BUILDING BRANCH 2: Tesseract (Standard Deployment)
echo ###########################################################

call :BUILD_STANDALONE "Branch2" "FreeSnip_tesseract" "USE_TESSERACT=true" "-p:PublishAot=false -p:SelfContained=true" "0"
if errorlevel 1 exit /b 1

copy /y "LICENSE.txt" ".\compiled\LICENSE.txt" >nul
call :VALIDATE_COMPILED_OUTPUT
if errorlevel 1 exit /b 1

echo.
echo ###########################################################
echo SUCCESS: Local dev build completed successfully.
echo.
echo Branch 1 (Native):    .\compiled\FreeSnip.exe
echo Branch 2 (Tesseract): .\compiled\FreeSnip_tesseract.exe
echo Log file:             .\build.log  (first line: OK / WARN / FAIL)
echo [PUBLISH] Skipped. Git and GitHub were not touched.
echo ###########################################################

exit /b 0

:BUILD_STANDALONE
set "BRANCH_NAME=%~1"
set "OUTPUT_NAME=%~2"
set "EXTRA_ARGS=%~3"
set "AOT_ARGS=%~4"
set "DROP_STAGED_EXE=%~5"

set "STAGING_DIR=.\obj\StandaloneTemp\%BRANCH_NAME%_staging"
set "FINAL_DIR=.\obj\StandaloneTemp\%BRANCH_NAME%_final"

echo [%BRANCH_NAME%] 1. Purging old temp folders...
if exist "%STAGING_DIR%" rd /s /q "%STAGING_DIR%"
if exist "%FINAL_DIR%" rd /s /q "%FINAL_DIR%"
if exist "src\freesnip\payload.zip" del /f /q "src\freesnip\payload.zip"

echo [%BRANCH_NAME%] 2. Publishing raw payload to staging...
dotnet publish "%PROJECT_FILE%" -c Release -r win-x64 %PUBLISH_BASE_ARGS% %AOT_ARGS% -p:%EXTRA_ARGS% !VERSION_ARGS! -o "%STAGING_DIR%" %DOTNET_LOG_ARGS%
if errorlevel 1 exit /b 1

echo [%BRANCH_NAME%] 2b. Stripping non-shipping runtime artifacts from payload...
call :STRIP_STAGING_BLOAT "%STAGING_DIR%" "%DROP_STAGED_EXE%"

echo [%BRANCH_NAME%] 3. Zipping payload...
powershell -NoProfile -Command "Compress-Archive -Path '%STAGING_DIR%\*' -DestinationPath 'src\freesnip\payload.zip' -Force" <nul

echo [%BRANCH_NAME%] 4. Publishing standalone installer...
dotnet publish "%PROJECT_FILE%" -c Release -r win-x64 %PUBLISH_BASE_ARGS% -p:PublishAot=true -p:EmbedOcrPayload=false -p:USE_TESSERACT=false !VERSION_ARGS! -o "%FINAL_DIR%" %DOTNET_LOG_ARGS%
if errorlevel 1 exit /b 1

echo [%BRANCH_NAME%] 5. Moving final EXE to compiled folder...
if not exist "%FINAL_DIR%\FreeSnip.exe" (
  echo ERROR: Expected standalone EXE was not produced in %FINAL_DIR%
  exit /b 1
)

move /y "%FINAL_DIR%\FreeSnip.exe" ".\compiled\%OUTPUT_NAME%.exe"
if errorlevel 1 exit /b 1

call :PURGE_COMPILED_EXTRAS

echo [%BRANCH_NAME%] 6. Cleaning up temporary artifacts...
if exist "%STAGING_DIR%" rd /s /q "%STAGING_DIR%"
if exist "%FINAL_DIR%" rd /s /q "%FINAL_DIR%"
if exist "src\freesnip\payload.zip" del /f /q "src\freesnip\payload.zip"

exit /b 0

:STRIP_STAGING_BLOAT
set "STRIP_DIR=%~1"
set "STRIP_EXE=%~2"
for %%D in (
  "Microsoft.DiaSymReader.Native.amd64.dll"
  "mscordaccore.dll"
  "mscordbi.dll"
  "createdump.exe"
  "clrgcexp.dll"
  "msquic.dll"
) do (
  if exist "%STRIP_DIR%\%%~D" (
    echo   [strip] %%~D
    del /f /q "%STRIP_DIR%\%%~D" >nul 2>&1
  )
)
for %%D in ("%STRIP_DIR%\mscordaccore_*.dll") do (
  echo   [strip] %%~nxD
  del /f /q "%%~fD" >nul 2>&1
)
if "%STRIP_EXE%"=="1" (
  if exist "%STRIP_DIR%\FreeSnip.exe" (
    echo   [strip] FreeSnip.exe ^(the installer already carries this build^)
    del /f /q "%STRIP_DIR%\FreeSnip.exe" >nul 2>&1
  )
)
exit /b 0

:PURGE_COMPILED_EXTRAS
for %%F in (".\compiled\*") do (
  if /I not "%%~xF"==".exe" if /I not "%%~nxF"=="LICENSE.txt" (
    echo ERROR: Removing disallowed artifact from compiled: %%~nxF
    rd /s /q "%%~fF" 2>nul
    del /f /q "%%~fF" 2>nul
    exit /b 1
  )
)
exit /b 0

:VALIDATE_COMPILED_OUTPUT
set "FILE_COUNT=0"
set "INVALID=0"
if not exist ".\compiled\FreeSnip.exe" set "INVALID=1"
if not exist ".\compiled\FreeSnip_tesseract.exe" set "INVALID=1"
if not exist ".\compiled\LICENSE.txt" set "INVALID=1"
for %%F in (".\compiled\*") do (
  set /a FILE_COUNT+=1
  if /I not "%%~xF"==".exe" if /I not "%%~nxF"=="LICENSE.txt" set "INVALID=1"
)
if not "!FILE_COUNT!"=="3" set "INVALID=1"
if "!INVALID!"=="1" (
  echo ERROR: .\compiled must contain exactly these files and nothing else:
  echo   FreeSnip.exe
  echo   FreeSnip_tesseract.exe
  echo   LICENSE.txt
  dir /b ".\compiled" 2>nul
  exit /b 1
)
echo Verified .\compiled contains exactly 2 EXE files and LICENSE.txt.
exit /b 0

:TERMINATE_PROCESSES
taskkill /F /IM FreeSnip.exe /T 2>nul
taskkill /F /IM FreeSnip_tesseract.exe /T 2>nul
taskkill /F /IM FreeSnip_Cleanup.exe /T 2>nul
dotnet build-server shutdown 2>nul
exit /b 0

:CLEAN_ALL
for /d /r . %%d in (bin obj) do @if exist "%%d" rd /s /q "%%d" 2>nul
exit /b 0
