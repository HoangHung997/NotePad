@echo off
setlocal EnableExtensions
chcp 65001 >nul
cd /d "%~dp0"
title H2Notes - REAL TWO-PC NAS TEST - PC1 / COORDINATOR

echo ==============================================================
echo H2Notes - REAL TWO-PC NAS TEST - PC1 / COORDINATOR
echo ==============================================================
echo.
echo IMPORTANT:
echo - Use the SAME newly downloaded bundle on PC1 and PC2.
echo - Use the SAME real NAS folder and SAME NEW Session ID.
echo - Use a dedicated test folder, NOT the live production .Note folder.
echo.

if not exist "H2Notes.NasAcceptance.exe" (
  echo ERROR: H2Notes.NasAcceptance.exe is missing from this folder.
  pause
  exit /b 2
)

echo Probe identity:
"H2Notes.NasAcceptance.exe" --info
if errorlevel 1 (
  echo ERROR: Could not read probe identity.
  pause
  exit /b 2
)
echo.

set "SHARED="
set /p "SHARED=Shared NAS test folder: "
if "%SHARED%"=="" (
  echo ERROR: Shared folder is required.
  pause
  exit /b 2
)
if not exist "%SHARED%\" (
  echo ERROR: Shared folder is not reachable: %SHARED%
  pause
  exit /b 2
)

set "SESSION="
set /p "SESSION=NEW Session ID - same on PC2 ^(example nas-byte-range-01^): "
if "%SESSION%"=="" (
  echo ERROR: Session ID is required.
  pause
  exit /b 2
)

set "OUT=%USERPROFILE%\Desktop\H2NasEvidence_PC1"
set "CUSTOMOUT="
set /p "CUSTOMOUT=Evidence folder [%OUT%]: "
if not "%CUSTOMOUT%"=="" set "OUT=%CUSTOMOUT%"

echo.
echo Starting coordinator...
echo Shared folder: %SHARED%
echo Session ID: %SESSION%
echo Evidence folder: %OUT%
echo.
"H2Notes.NasAcceptance.exe" --node coordinator "%SHARED%" "%SESSION%" "%OUT%"
set "RC=%ERRORLEVEL%"

echo.
if "%RC%"=="0" (
  echo PC1 RESULT: PASS
) else (
  echo PC1 RESULT: FAIL ^(exit code %RC%^)
)
echo Evidence: %OUT%\nas-acceptance-coordinator.json
echo.
pause
exit /b %RC%
