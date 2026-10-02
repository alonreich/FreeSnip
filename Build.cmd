@echo off
set "DOTNET_CLI_FORCE_UTF8_ENCODING=false"
set "DOTNET_CLI_UI_LANGUAGE=en-US"
set "VSCONSOLEOUTPUT=1"
set "SCRIPT_DIR=%~dp0"
pushd "%SCRIPT_DIR%" >nul || exit /b 1
rem ---------------------------------------------------------------------------

rem  Build.cmd â€” dual output: live console + .\build.log (ANSI colors in log).

rem  Log is deleted at the start of each run; header/footer include timestamps.

rem ---------------------------------------------------------------------------

rem  THE ONLY PLACE THAT PAUSES: the real run happens inside BuildLog.ps1's
rem  pipeline, so a `pause` there would prompt into the pipe and look like a
rem  hang. The wrapper below is deliberately FLAT sequential lines, NOT one
rem  big parenthesized block: cmd expands %VARIABLES% when it reads an entire
rem  block at once, which would freeze BUILD_EXIT at its pre-build value and
rem  silently break both the pauses and the returned exit code.
rem      0 = build OK and release published (or publishing was not requested)
rem      1 = BUILD FAILED           -> nothing was published, the release is untouched
rem      2 = build OK, PUBLISH DID NOT COMPLETE -> local exes are good, GitHub not updated
rem  Success is silent so a clean run needs no keypress.

if /I "%~1"=="__BUILD_LOGGED__" goto :run_logged

  powershell -NoProfile -ExecutionPolicy Bypass -File ".\developer_tools\BuildLog.ps1" %* <nul

  set "BUILD_EXIT=%ERRORLEVEL%"

  rem Exit code legend:
  rem   0 = build OK and release published (or publishing was not requested)
  rem   1 = BUILD FAILED           -> nothing was published, the release is untouched
  rem   2 = build OK, PUBLISH DID NOT COMPLETE -> local exes are good, GitHub not updated
  if "%BUILD_EXIT%"=="1" (
    echo.
    echo ###########################################################
    echo  BUILD FAILED - nothing was published.
    echo  The existing GitHub release was NOT touched.
    echo  Scroll up for the first ERROR line, or read .\build.log
    echo ###########################################################
    pause
  )
  if "%BUILD_EXIT%"=="2" (
    echo.
    echo ###########################################################
    echo  BUILD OK - but the release was NOT updated.
    echo  .\compiled\ holds good, usable exes.
    echo  Only the GitHub publish step did not finish - reason above.
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

rem --no-publish = compile only, leave GitHub alone. Everything else needs no arguments.
set "DO_PUBLISH=1"
if /I "%~1"=="--no-publish" set "DO_PUBLISH=0"

rem ---------------------------------------------------------------------------
rem  VERSION STAMP (pattern ported from the reference build script): every
rem  release used to report an unstamped version, so a bug report could not be
rem  tied to a binary and two builds on the same day collided on one tag. The
rem  version is computed ONCE here so both exes, the release tag and the
rem  release notes all say the same thing.
rem    yyyy.MM.dd.HHmm - each part must stay under 65535 for a Windows version
rem    resource. HHmm does; HHmmss does not.
rem ---------------------------------------------------------------------------
set "BUILD_VERSION="
for /f "usebackq delims=" %%V in (`powershell -NoProfile -Command "Get-Date -Format yyyy.MM.dd.HHmm"`) do set "BUILD_VERSION=%%V"
if not defined BUILD_VERSION (
  echo ERROR: could not compute a build version.
  exit /b 1
)
set "TAG=v!BUILD_VERSION!"
set "VERSION_ARGS=-p:Version=!BUILD_VERSION! -p:FileVersion=!BUILD_VERSION! -p:InformationalVersion=!BUILD_VERSION!"
echo Build version: !BUILD_VERSION!  ^(tag !TAG!^)

powershell -NoProfile -Command "Set-Content -Path '.\version.txt' -Value '!BUILD_VERSION!' -NoNewline; Set-Content -Path '.\Directory.Build.props' -Value ('<Project>`r`n  <PropertyGroup>`r`n    <Version>!BUILD_VERSION!</Version>`r`n    <AssemblyVersion>!BUILD_VERSION!</AssemblyVersion>`r`n    <FileVersion>!BUILD_VERSION!</FileVersion>`r`n    <InformationalVersion>!BUILD_VERSION!</InformationalVersion>`r`n    <ProductVersion>!BUILD_VERSION!</ProductVersion>`r`n  </PropertyGroup>`r`n</Project>')" <nul

echo ###########################################################

echo PURGING PREVIOUS BUILD ARTIFACTS...

echo ###########################################################

rem A running FreeSnip/FreeSnip_tesseract instance keeps .\compiled locked and the
rem wipe below silently half-fails. Kill both flavors first.
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

echo BUILDING BRANCH 1: Native

echo ###########################################################

rem Soft probe only: the .NET SDK can locate the MSVC toolchain by itself even
rem when link.exe is not on PATH, so this must not gate the build (the wording
rem deliberately avoids the word error/warning - BuildLog.ps1 greps for those).
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

echo SUCCESS: Build completed successfully.

echo.

echo Branch 1 (Native): .\compiled\FreeSnip.exe

echo Branch 2 (Tesseract):   .\compiled\FreeSnip_tesseract.exe

echo Log file:               .\build.log  (first line: OK / WARN / FAIL)

echo ###########################################################

if "!DO_PUBLISH!"=="0" (
  echo.
  echo [PUBLISH] Skipped on request ^(--no-publish^). GitHub was not touched.
  exit /b 0
)

echo.

echo ###########################################################

echo PUBLISHING RELEASE TO GITHUB...

echo ###########################################################

call :PUBLISH_RELEASE

if errorlevel 1 exit /b 2

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

:DETECT_NATIVE_AOT

where link.exe >nul 2>&1

if not errorlevel 1 goto DETECT_NATIVE_AOT_OK

where vswhere.exe >nul 2>&1

if errorlevel 1 (
  echo ERROR: Native AOT platform linker ^(link.exe^) not found in PATH.
  echo ERROR: Open a Developer Command Prompt or add Visual Studio tools to PATH.
  exit /b 1
)

for /f "usebackq delims=" %%I in (`vswhere.exe -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath 2^>nul`) do (
  if exist "%%I\Common7\Tools\VsDevCmd.bat" (
    call "%%I\Common7\Tools\VsDevCmd.bat" -arch=x64 -host_arch=x64 >nul
    where link.exe >nul 2>&1
    if not errorlevel 1 goto DETECT_NATIVE_AOT_OK
  )
)

echo ERROR: Native AOT platform linker ^(link.exe^) not found.
echo ERROR: Open a Developer Command Prompt or add Visual Studio tools to PATH.
exit /b 1

:DETECT_NATIVE_AOT_OK

echo Native AOT toolchain detected.
exit /b 0

:TERMINATE_PROCESSES

taskkill /F /IM FreeSnip.exe /T 2>nul
taskkill /F /IM FreeSnip_tesseract.exe /T 2>nul
taskkill /F /IM FreeSnip_Cleanup.exe /T 2>nul
dotnet build-server shutdown 2>nul
exit /b 0



:SLEEP_SEC

set /a "_sleep_ping=%~1+1"

ping 127.0.0.1 -n !_sleep_ping! >nul 2>&1

exit /b 0



:CLEAN_ALL

for /d /r . %%d in (bin obj) do @if exist "%%d" rd /s /q "%%d" 2>nul

exit /b 0



:UNLOCK_MAIN_EXE

exit /b 0




rem =====================================================================================
rem  :PUBLISH_RELEASE - replace the one-and-only GitHub release with what was just built.
rem
rem  ONLY REACHED WHEN THE BUILD SUCCEEDED. dotnet publish runs with
rem  -p:TreatWarningsAsErrors=true, so "no errors" is already enforced upstream; if any
rem  stage above failed the script exited 1 long before this point and GitHub is untouched.
rem
rem  ZERO MAINTENANCE BY DESIGN - nothing in here is hardcoded:
rem    * the repository is resolved from THIS FOLDER'S git remote via `gh repo view`,
rem      so renaming or forking the repo needs no edit here;
rem    * the tag comes from the version stamp computed at the top of this run;
rem    * every pre-existing release is enumerated and removed, so "latest and only"
rem      stays true without anyone tracking version numbers.
rem =====================================================================================
:PUBLISH_RELEASE

where gh >nul 2>&1
if errorlevel 1 (
  echo [PUBLISH] STOPPED: GitHub CLI ^(gh^) is not installed or not on PATH.
  echo [PUBLISH] Fix: install from https://cli.github.com  -  or run  .\Build.cmd --no-publish
  exit /b 1
)

gh auth status >nul 2>&1
if errorlevel 1 (
  echo [PUBLISH] STOPPED: gh is installed but not signed in.
  echo [PUBLISH] Fix: run  gh auth login
  exit /b 1
)
echo [PUBLISH] 1/7 GitHub CLI found and signed in.                   [OK]

set "REPO="
for /f "usebackq delims=" %%R in (`gh repo view --json nameWithOwner --jq .nameWithOwner 2^>nul`) do set "REPO=%%R"
if not defined REPO (
  echo [PUBLISH] STOPPED: could not work out the GitHub repository for this folder.
  echo [PUBLISH] Fix: confirm  git remote -v  points at GitHub and you can reach the network.
  exit /b 1
)
echo [PUBLISH] 2/7 Target repository: !REPO!                         [OK]

call :GET_SHA256 ".\compiled\FreeSnip.exe"
if not defined SHA256_OUT (
  echo [PUBLISH] STOPPED: could not fingerprint .\compiled\FreeSnip.exe
  exit /b 1
)
set "HASH_NATIVE=!SHA256_OUT!"

call :GET_SHA256 ".\compiled\FreeSnip_tesseract.exe"
if not defined SHA256_OUT (
  echo [PUBLISH] STOPPED: could not fingerprint .\compiled\FreeSnip_tesseract.exe
  exit /b 1
)
set "HASH_TESS=!SHA256_OUT!"
echo [PUBLISH] 3/7 Both built exes fingerprinted, tag !TAG! ready.   [OK]

rem ---------------------------------------------------------------------------
rem  THERE IS EXACTLY ONE RELEASE, EVERYWHERE, ALWAYS - NO EXCEPTIONS.
rem    local : :VALIDATE_COMPILED_OUTPUT proves .\compiled holds exactly the
rem           three shipped files, and the purge at the top of this run wiped
rem           the folder before anything was written into it.
rem    cloud : EVERY previous release is deleted below, whatever its tag.
rem    git   : and every tag with it. `--cleanup-tag` only removes a tag owned
rem           by the release being deleted; a tag created by hand, or one left
rem           behind when a release delete half-failed, survives it and still
rem           shows up as another "version". The sweep below removes every
rem           remaining tag remotely AND locally. The slate is then RE-READ
rem           before publishing onto it: a delete that silently failed must
rem           not be counted as a success.
rem ---------------------------------------------------------------------------
set "REMOVED=0"
for /f "usebackq delims=" %%T in (`gh release list --repo !REPO! --limit 200 --json tagName --jq ".[].tagName" 2^>nul`) do (
  echo [PUBLISH]     removing previous release %%T
  gh release delete %%T --repo !REPO! --cleanup-tag --yes >nul 2>&1
  set /a REMOVED+=1
)

git fetch --tags --prune --prune-tags >nul 2>&1
for /f "usebackq delims=" %%T in (`git tag --list 2^>nul`) do (
  git push origin --delete "%%T" >nul 2>&1
  git tag -d "%%T" >nul 2>&1
)

set "SURVIVORS="
for /f "usebackq delims=" %%T in (`gh release list --repo !REPO! --limit 200 --json tagName --jq ".[].tagName" 2^>nul`) do set "SURVIVORS=!SURVIVORS! %%T"
if defined SURVIVORS (
  echo [PUBLISH] STOPPED: these releases could not be removed:!SURVIVORS!
  echo [PUBLISH] Publishing now would leave more than one release live. Remove them by hand.
  exit /b 1
)
echo [PUBLISH] 4/7 Removed !REMOVED! previous release^(s^) and all tags.    [OK]

gh release create !TAG! ".\compiled\FreeSnip.exe#FreeSnip.exe - Native AOT" ".\compiled\FreeSnip_tesseract.exe#FreeSnip_tesseract.exe - with OCR" ".\compiled\LICENSE.txt#LICENSE.txt" --repo !REPO! --title "FreeSnip !TAG!" --notes "Automated release published by Build.cmd on !TAG!. This is the only release: every previous release and tag is deleted on each publish. FreeSnip.exe SHA256 !HASH_NATIVE! / FreeSnip_tesseract.exe SHA256 !HASH_TESS!." --latest >nul 2>&1
if errorlevel 1 (
  echo [PUBLISH] STOPPED: creating release !TAG! failed.
  echo [PUBLISH] Your build is fine - only the upload failed. Retry, or publish by hand.
  exit /b 1
)
echo [PUBLISH] 5/7 Release !TAG! created, 3 assets uploaded.              [OK]

rem  THE UPLOAD IS VERIFIED BY HASH, NOT BY EXIT CODE: gh reports success even
rem  when the asset that ends up attached is not the file you meant to send.
rem  The check below compares the SHA256 digests GitHub reports for BOTH exes
rem  against the freshly built files. The whole pipeline lives inside one
rem  quoted powershell -Command string, so no cmd for /f escaping games.
powershell -NoProfile -Command "$assets = (gh release view !TAG! --repo !REPO! --json assets | ConvertFrom-Json).assets; $assets | ForEach-Object { Write-Host ('[PUBLISH]     remote: ' + $_.name + ' ' + $_.digest) }; $n = $assets | Where-Object name -eq 'FreeSnip.exe'; $t = $assets | Where-Object name -eq 'FreeSnip_tesseract.exe'; if (-not $n -or -not $t -or $n.digest.ToLower() -ne ('sha256:!HASH_NATIVE!').ToLower() -or $t.digest.ToLower() -ne ('sha256:!HASH_TESS!').ToLower()) { exit 1 }; exit 0" <nul
if errorlevel 1 (
  echo [PUBLISH] STOPPED: an uploaded asset does NOT match the file that was just built.
  echo [PUBLISH]     FreeSnip.exe built            sha256:!HASH_NATIVE!
  echo [PUBLISH]     FreeSnip_tesseract built      sha256:!HASH_TESS!
  echo [PUBLISH] The release would be serving the WRONG binary - fix before telling anyone.
  exit /b 1
)
echo [PUBLISH] 6/7 Both uploaded exe hashes match the built files.    [OK]

echo [PUBLISH] 7/7 Done. https://github.com/!REPO!/releases now holds exactly one release.

echo.
echo ###########################################################
echo SUCCESS: release !TAG! is live and is the only release.
echo Download: https://github.com/!REPO!/releases/latest/download/FreeSnip.exe
echo ###########################################################
exit /b 0

rem  Fingerprint helper: %1 = file, result lands in SHA256_OUT (UPPERCASE hex).
rem  certutil prints the header on line 1 and the hash on line 2 - take line 2.
:GET_SHA256
set "SHA256_OUT="
for /f "skip=1 delims=" %%H in ('certutil -hashfile "%~1" SHA256') do (
  if not defined SHA256_OUT set "SHA256_OUT=%%H"
)
set "SHA256_OUT=!SHA256_OUT: =!"
exit /b 0


