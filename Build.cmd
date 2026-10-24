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
rem  CHRONOLOGICAL VERSION STAMP:
rem  Every release reports a timestamp version yyyy.MM.dd.HHmm so that:
rem    1. It strictly stays under 65535 per field for Windows FILEVERSION.
rem    2. It strictly increases chronologically, ensuring backward compatibility
rem       with existing installed clients (e.g. v2026.10.7.935).
rem  Supports explicit override via: Build.cmd <version>
rem ---------------------------------------------------------------------------
set "BUILD_VERSION="
set "USER_ARG=%~1"
if defined USER_ARG if /I not "!USER_ARG!"=="--no-publish" if /I not "!USER_ARG!"=="--patch" (
  set "BUILD_VERSION=!USER_ARG!"
  shift
)
if not defined BUILD_VERSION (
  for /f "usebackq delims=" %%V in (`powershell -NoProfile -Command "Get-Date -Format yyyy.MM.dd.HHmm"`) do set "BUILD_VERSION=%%V"
)
if not defined BUILD_VERSION (
  echo ERROR: could not compute a build version.
  exit /b 1
)

set "FILE_VERSION="
for /f "usebackq delims=" %%V in (`powershell -NoProfile -Command "$v = [version]'!BUILD_VERSION!'; if ($v.Build -lt 0) { '!BUILD_VERSION!.0.0' } elseif ($v.Revision -lt 0) { '!BUILD_VERSION!.0' } else { '!BUILD_VERSION!' }"`) do set "FILE_VERSION=%%V"
if not defined FILE_VERSION set "FILE_VERSION=!BUILD_VERSION!"

set "GIT_COMMIT="
for /f "usebackq delims=" %%G in (`git rev-parse --short HEAD 2^>nul`) do set "GIT_COMMIT=%%G"
if not defined GIT_COMMIT set "GIT_COMMIT=unknown"
set "INFORMATIONAL_VERSION=!BUILD_VERSION!+!GIT_COMMIT!"
set "TAG=v!BUILD_VERSION!"
set "VERSION_ARGS=-p:Version=!BUILD_VERSION! -p:FileVersion=!FILE_VERSION! -p:AssemblyVersion=!FILE_VERSION! -p:InformationalVersion=!INFORMATIONAL_VERSION!"
echo Build version: !BUILD_VERSION!  ^(tag !TAG!, fileversion !FILE_VERSION!, commit !GIT_COMMIT!^)

powershell -NoProfile -Command "$lines = @('<Project>', '  <PropertyGroup>', '    <Version>!BUILD_VERSION!</Version>', '    <AssemblyVersion>!FILE_VERSION!</AssemblyVersion>', '    <FileVersion>!FILE_VERSION!</FileVersion>', '    <InformationalVersion>!INFORMATIONAL_VERSION!</InformationalVersion>', '    <ProductVersion>!BUILD_VERSION!</ProductVersion>', '  </PropertyGroup>', '</Project>'); Set-Content -Path '.\version.txt' -Value '!BUILD_VERSION!' -NoNewline; Set-Content -Path '.\Directory.Build.props' -Value $lines" <nul

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

call :SIGN_NATIVE_DEPENDENCIES
if errorlevel 1 exit /b 1

call :BUILD_STANDALONE "Branch2" "FreeSnip_tesseract" "USE_TESSERACT=true" "-p:PublishAot=false -p:SelfContained=true" "0"

if errorlevel 1 exit /b 1

copy /y "LICENSE.txt" ".\compiled\LICENSE.txt" >nul
call :VALIDATE_COMPILED_OUTPUT

if errorlevel 1 exit /b 1

call :APPLY_VERSION_RESOURCES

if errorlevel 1 exit /b 1

call :SIGN_BINARIES

if errorlevel 1 exit /b 1

echo.
echo ###########################################################
echo GENERATING DELTA UPDATE PACKAGES...
echo ###########################################################
powershell -NoProfile -Command "Compress-Archive -Path '.\compiled\FreeSnip.exe' -DestinationPath '.\compiled\FreeSnip_delta.zip' -Force" <nul
powershell -NoProfile -Command "Compress-Archive -Path '.\compiled\FreeSnip_tesseract.exe' -DestinationPath '.\compiled\FreeSnip_tesseract_delta.zip' -Force" <nul

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
powershell -NoProfile -Command "Start-Sleep -Seconds 1" <nul

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
  if /I not "%%~xF"==".exe" if /I not "%%~xF"==".zip" if /I not "%%~nxF"=="LICENSE.txt" (
    echo ERROR: Removing disallowed artifact from compiled: %%~nxF
    rd /s /q "%%~fF" 2>nul
    del /f /q "%%~fF" 2>nul
    exit /b 1
  )
)
exit /b 0

:VALIDATE_COMPILED_OUTPUT
set "INVALID=0"
if not exist ".\compiled\FreeSnip.exe" set "INVALID=1"
if not exist ".\compiled\FreeSnip_tesseract.exe" set "INVALID=1"
if not exist ".\compiled\LICENSE.txt" set "INVALID=1"
for %%F in (".\compiled\*") do (
  if /I not "%%~xF"==".exe" if /I not "%%~xF"==".zip" if /I not "%%~nxF"=="LICENSE.txt" set "INVALID=1"
)
if "!INVALID!"=="1" (
  echo ERROR: .\compiled must contain only FreeSnip binaries, delta archives, and LICENSE.txt
  dir /b ".\compiled" 2>nul
  exit /b 1
)
echo Verified .\compiled contains valid FreeSnip release assets and LICENSE.txt.
exit /b 0

:SIGN_NATIVE_DEPENDENCIES
echo.
echo ###########################################################
echo DIGITALLY SIGNING BUNDLED NATIVE DEPENDENCIES...
echo ###########################################################
where pwsh >nul 2>&1
if not errorlevel 1 (
  pwsh -NoProfile -ExecutionPolicy Bypass -File ".\developer_tools\SignBinaries.ps1" -TargetFolder ".\src\freesnip\Native\x64" <nul
) else (
  powershell -ExecutionPolicy Bypass -File ".\developer_tools\SignBinaries.ps1" -TargetFolder ".\src\freesnip\Native\x64" <nul
)
if errorlevel 1 (
  echo ERROR: Digital signature on native dependencies failed.
  exit /b 1
)
exit /b 0

:APPLY_VERSION_RESOURCES
echo.
echo ###########################################################
echo SYNCHRONIZING PE VERSION METADATA...
echo ###########################################################
where pwsh >nul 2>&1
if not errorlevel 1 (
  pwsh -NoProfile -ExecutionPolicy Bypass -File ".\developer_tools\ApplyVersionResource.ps1" -ExePath ".\compiled\FreeSnip.exe" -Version "!BUILD_VERSION!" -OriginalFilename "FreeSnip.exe" -InternalName "FreeSnip.exe" -FileDescription "FreeSnip" <nul
  if errorlevel 1 exit /b 1
  pwsh -NoProfile -ExecutionPolicy Bypass -File ".\developer_tools\ApplyVersionResource.ps1" -ExePath ".\compiled\FreeSnip_tesseract.exe" -Version "!BUILD_VERSION!" -OriginalFilename "FreeSnip_tesseract.exe" -InternalName "FreeSnip_tesseract.exe" -FileDescription "FreeSnip (Tesseract OCR)" <nul
  if errorlevel 1 exit /b 1
) else (
  powershell -NoProfile -ExecutionPolicy Bypass -File ".\developer_tools\ApplyVersionResource.ps1" -ExePath ".\compiled\FreeSnip.exe" -Version "!BUILD_VERSION!" -OriginalFilename "FreeSnip.exe" -InternalName "FreeSnip.exe" -FileDescription "FreeSnip" <nul
  if errorlevel 1 exit /b 1
  powershell -NoProfile -ExecutionPolicy Bypass -File ".\developer_tools\ApplyVersionResource.ps1" -ExePath ".\compiled\FreeSnip_tesseract.exe" -Version "!BUILD_VERSION!" -OriginalFilename "FreeSnip_tesseract.exe" -InternalName "FreeSnip_tesseract.exe" -FileDescription "FreeSnip (Tesseract OCR)" <nul
  if errorlevel 1 exit /b 1
)
exit /b 0

:SIGN_BINARIES
echo.
echo ###########################################################
echo DIGITALLY SIGNING COMPILED BINARIES (AUTHENTICODE)...
echo ###########################################################
where pwsh >nul 2>&1
if not errorlevel 1 (
  pwsh -NoProfile -ExecutionPolicy Bypass -File ".\developer_tools\SignBinaries.ps1" -TargetFolder ".\compiled" <nul
) else (
  powershell -ExecutionPolicy Bypass -File ".\developer_tools\SignBinaries.ps1" -TargetFolder ".\compiled" <nul
)
if errorlevel 1 (
  echo ERROR: Digital signature process failed.
  exit /b 1
)
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

call :GET_SHA256 ".\compiled\FreeSnip_delta.zip"
set "HASH_NATIVE_DELTA=!SHA256_OUT!"

call :GET_SHA256 ".\compiled\FreeSnip_tesseract_delta.zip"
set "HASH_TESS_DELTA=!SHA256_OUT!"

echo [PUBLISH] 3/7 Built standalone exes and delta archives fingerprinted, tag !TAG! ready.   [OK]

rem ---------------------------------------------------------------------------
rem  RELEASE RETENTION POLICY:
rem    Keep all releases from the last 90 days.
rem    Always keep at least the latest 3 releases, regardless of age.
rem    Only prune releases that are BOTH older than 90 days AND outside top 3.
rem ---------------------------------------------------------------------------
echo [PUBLISH] 4/7 Applying release retention policy ^(keep 90 days or top 3^)...
powershell -NoProfile -Command "$releases = gh release list --repo !REPO! --limit 200 --json tagName,createdAt | ConvertFrom-Json; $now = [DateTime]::UtcNow; $cutoff = $now.AddDays(-90); $idx = 0; $pruned = 0; foreach ($r in $releases) { $idx++; $created = [DateTime]::Parse($r.createdAt).ToUniversalTime(); if ($idx -le 3 -or $created -ge $cutoff) { Write-Host ('[PUBLISH]     keeping release ' + $r.tagName + ' (created ' + $r.createdAt + ', rank ' + $idx + ')') } else { Write-Host ('[PUBLISH]     pruning expired release ' + $r.tagName + ' (created ' + $r.createdAt + ')'); gh release delete $r.tagName --repo !REPO! --cleanup-tag --yes >$null 2>&1; $pruned++ } }; Write-Host ('[PUBLISH]     Retention completed. Pruned ' + $pruned + ' expired release(s).')" <nul
echo [PUBLISH] 4/7 Release retention check finished.                      [OK]

gh release view !TAG! --repo !REPO! >nul 2>&1
if not errorlevel 1 (
  echo [PUBLISH]     Release !TAG! already exists; refreshing release assets...
  gh release delete !TAG! --repo !REPO! --cleanup-tag --yes >nul 2>&1
  git push origin --delete "!TAG!" >nul 2>&1
  git tag -d "!TAG!" >nul 2>&1
)

powershell -NoProfile -Command "$raw = if (Test-Path '.\docs\CHANGELOG.md') { Get-Content '.\docs\CHANGELOG.md' -Raw } else { '' }; $m = [regex]::Match($raw, '(?s)###\s+\[(.*?)\]\s*-\s*([0-9\-]+)\r?\n(.*?)(?=\r?\n---\r?\n|\z)'); $notesBody = if ($m.Success) { $m.Groups[3].Value.Trim() } else { 'Performance enhancements and bug fixes.' }; $notes = @('# What''s New in this Release', '', $notesBody, '', '---', '### Checksums (SHA-256):', '- FreeSnip.exe: sha256:!HASH_NATIVE!', '- FreeSnip_delta.zip: sha256:!HASH_NATIVE_DELTA!', '- FreeSnip_tesseract.exe: sha256:!HASH_TESS!', '- FreeSnip_tesseract_delta.zip: sha256:!HASH_TESS_DELTA!', '', 'Digitally signed with Authenticode and RFC 3161 timestamp.') -join [Environment]::NewLine; Set-Content -Path '.\compiled\release_notes.txt' -Value $notes -Encoding utf8" <nul

gh release create !TAG! ".\compiled\FreeSnip.exe#FreeSnip.exe - Native AOT" ".\compiled\FreeSnip_delta.zip#FreeSnip_delta.zip - Delta Update" ".\compiled\FreeSnip_tesseract.exe#FreeSnip_tesseract.exe - with OCR" ".\compiled\FreeSnip_tesseract_delta.zip#FreeSnip_tesseract_delta.zip - Delta Update" ".\compiled\LICENSE.txt#LICENSE.txt" --repo !REPO! --title "FreeSnip !TAG!" --notes-file ".\compiled\release_notes.txt" --latest >nul 2>&1
if errorlevel 1 (
  echo [PUBLISH] STOPPED: creating release !TAG! failed.
  echo [PUBLISH] Your build is fine - only the upload failed. Retry, or publish by hand.
  exit /b 1
)
echo [PUBLISH] 5/7 Release !TAG! created, 5 assets uploaded.              [OK]

rem  THE UPLOAD IS VERIFIED BY HASH, NOT BY EXIT CODE: gh reports success even
rem  when the asset that ends up attached is not the file you meant to send.
powershell -NoProfile -Command "$assets = (gh release view !TAG! --repo !REPO! --json assets | ConvertFrom-Json).assets; $assets | ForEach-Object { Write-Host ('[PUBLISH]     remote: ' + $_.name + ' ' + $_.digest) }; $n = $assets | Where-Object name -eq 'FreeSnip.exe'; $t = $assets | Where-Object name -eq 'FreeSnip_tesseract.exe'; $nd = $assets | Where-Object name -eq 'FreeSnip_delta.zip'; $td = $assets | Where-Object name -eq 'FreeSnip_tesseract_delta.zip'; if (-not $n -or -not $t -or -not $nd -or -not $td -or $n.digest.ToLower() -ne ('sha256:!HASH_NATIVE!').ToLower() -or $t.digest.ToLower() -ne ('sha256:!HASH_TESS!').ToLower() -or $nd.digest.ToLower() -ne ('sha256:!HASH_NATIVE_DELTA!').ToLower() -or $td.digest.ToLower() -ne ('sha256:!HASH_TESS_DELTA!').ToLower()) { exit 1 }; exit 0" <nul
if errorlevel 1 (
  echo [PUBLISH] STOPPED: an uploaded asset does NOT match the file that was just built.
  echo [PUBLISH]     FreeSnip.exe built                  sha256:!HASH_NATIVE!
  echo [PUBLISH]     FreeSnip_delta.zip built            sha256:!HASH_NATIVE_DELTA!
  echo [PUBLISH]     FreeSnip_tesseract built            sha256:!HASH_TESS!
  echo [PUBLISH]     FreeSnip_tesseract_delta.zip built  sha256:!HASH_TESS_DELTA!
  echo [PUBLISH] The release would be serving the WRONG binary - fix before telling anyone.
  exit /b 1
)
echo [PUBLISH] 6/7 All uploaded asset hashes match the built files.   [OK]

echo [PUBLISH] 7/7 Done. Release !TAG! published.

echo.
echo ###########################################################
echo SUCCESS: Release !TAG! is live.
echo Download: https://github.com/!REPO!/releases/latest/download/FreeSnip.exe
echo ###########################################################
echo.
echo ===========================================================
echo  DEFENDER WHITELIST SUBMISSION (FAST-TRACK FALSE POSITIVE CLEARANCE):
echo  To immediately prevent Defender warnings on fresh download:
echo  1. Open: https://www.microsoft.com/en-us/wdsi/filesubmission
echo  2. Category: Software Developer -^> Incorrectly detected as malware
echo  3. Upload .\compiled\FreeSnip.exe
echo     SHA256: !HASH_NATIVE!
echo  4. Upload .\compiled\FreeSnip_tesseract.exe
echo     SHA256: !HASH_TESS!
echo ===========================================================
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


