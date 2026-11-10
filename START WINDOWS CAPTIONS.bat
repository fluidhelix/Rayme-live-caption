@echo off
setlocal
cd /d "%~dp0"
title Windows Live Captions
where node >nul 2>nul
if errorlevel 1 (
  echo Node.js is missing. Install Node.js 22.12 or newer from nodejs.org.
  pause
  exit /b 1
)
if not exist "native\bin\LoopbackCapture.exe" (
  echo Download windows-loopback-win-x64 from GitHub Actions.
  echo Extract LoopbackCapture.exe into native\bin. See WINDOWS-CAPTIONS.md.
  pause
  exit /b 1
)
if not exist "node_modules" (
  call npm ci --no-audit --no-fund
  if errorlevel 1 (
    echo Dependency installation failed. Check your connection and retry.
    pause
    exit /b 1
  )
)
node windows-captions\main.js
if errorlevel 1 pause
endlocal
