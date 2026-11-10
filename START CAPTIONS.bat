@echo off
title Live Captions for Rayme
color 0B
cd /d "%~dp0"

cls
echo.
echo   ==========================================
echo      LIVE CAPTIONS  -  made for Rayme
echo                by Shator
echo   ==========================================
echo.

REM ---------- Check Node.js is installed ----------
where node >nul 2>nul
if errorlevel 1 (
  color 0C
  echo   [X] Node.js is not installed on this PC.
  echo.
  echo   Please install it first:
  echo     1. Go to  https://nodejs.org
  echo     2. Download the big green "LTS" button
  echo     3. Run the installer, click Next until it finishes
  echo     4. Then double-click this file again
  echo.
  pause
  exit /b
)

REM ---------- Check the settings file exists ----------
if not exist ".env" (
  color 0C
  echo   [X] The .env settings file is missing.
  echo.
  echo   This file holds the bot token. Ask Shator for it.
  echo.
  pause
  exit /b
)

REM ---------- Check the whisper folder exists ----------
if not exist "whisper" (
  color 0C
  echo   [X] The "whisper" folder is missing.
  echo.
  echo   That folder holds the speech recognition files.
  echo   Ask Shator to send the complete folder.
  echo.
  pause
  exit /b
)

REM ---------- Install dependencies on first run ----------
if not exist "node_modules" (
  echo   First time setup - this happens only once.
  echo   Installing... this can take 1-2 minutes, please wait.
  echo.
  call npm install
  if errorlevel 1 (
    color 0C
    echo.
    echo   [X] Setup failed. Please send Shator a screenshot of this window.
    echo.
    pause
    exit /b
  )
  cls
  echo.
  echo   Setup complete!
  echo.
)

REM ---------- Open the captions window ----------
echo   Opening the captions window...
start "" chrome.exe "file:///%CD:\=/%/overlay.html" 2>nul
if errorlevel 1 start "" "overlay.html"

timeout /t 2 /nobreak >nul

cls
echo.
echo   ==========================================
echo      LIVE CAPTIONS  -  made for Rayme
echo   ==========================================
echo.
echo   Starting up... the first time takes a few seconds
echo   while the speech model loads.
echo.
echo   WHAT TO DO:
echo     1. Wait for "Listening" to appear below
echo     2. Join a voice channel in Discord
echo     3. Captions appear in the Chrome window
echo     4. Click "Pop out" to float it over your game
echo.
echo   TIP: Set Valorant to BORDERLESS (not Fullscreen)
echo        so the captions can show on top.
echo.
echo   To stop: just close this window.
echo.
echo   ------------------------------------------
echo.

node index.js

REM ---------- If we get here, the bot stopped ----------
color 0E
echo.
echo   ------------------------------------------
echo   The captions bot has stopped.
echo.
echo   If this was not on purpose, screenshot this
echo   window and send it to Shator.
echo.
pause
