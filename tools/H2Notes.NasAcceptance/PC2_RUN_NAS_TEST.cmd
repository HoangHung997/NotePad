@echo off
setlocal EnableExtensions
chcp 65001 >nul
cd /d "%~dp0"
title H2Notes - REAL TWO-PC NAS TEST - PC2 / PEER

echo ==============================================================
echo H2Notes - REAL TWO-PC NAS TEST - PC2 / PEER
echo ==============================================================
echo.
echo IMPORTANT:
echo - Start PC1 coordinator first, then run this file on PC2.
echo - Use the SAME newly downloaded bundle on PC1 and PC2.
echo - Use the SAME physical NAS folder and SAME NEW Session ID.
echo - Local mapped paths may differ between the two PCs.
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
set /p "SHARED=Shared NAS test folder as seen from PC2: "
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
set /p "SESSION=NEW Session ID - exactly the same as PC1: "
if "%SESSION%"=="" (
  echo ERROR: Session ID is required.
  pause
  exit /b 2
)

set "OUT=%USERPROFILE%\Desktop\H2NasEvidence_PC2"
set "CUSTOMOUT="
set /p "CUSTOMOUT=Evidence folder [%OUT%]: "
if not "%CUSTOMOUT%"=="" set "OUT=%CUSTOMOUT%"

echo.
echo Starting peer...
echo Shared folder: %SHARED%
echo Session ID: %SESSION%
echo Evidence folder: %OUT%
echo.
"H2Notes.NasAcceptance.exe" --node peer "%SHARED%" "%SESSION%" "%OUT%"
set "RC=%ERRORLEVEL%"

echo.
if "%RC%"=="0" (
  echo PC2 RESULT: PASS
) else (
  echo PC2 RESULT: FAIL ^(exit code %RC%^)
)
echo Evidence: %OUT%\nas-acceptance-peer.json
echo.
pause
exit /b %RC%
