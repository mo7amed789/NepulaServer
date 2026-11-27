@echo off
setlocal enabledelayedexpansion

rem ============================================================
rem  Export YouTube cookies for NepulaServer / yt-dlp
rem
rem  Writes %ProgramData%\Tanzeel\cookies.txt, which both the
rem  extraction path and the download engine pick up automatically.
rem  The browser must be CLOSED: Chrome/Edge lock their cookie
rem  database while running and yt-dlp then reports
rem  "Could not copy Chrome cookie database".
rem ============================================================

set "BROWSER=%~1"
if "%BROWSER%"=="" set "BROWSER=chrome"

set "TARGET=%ProgramData%\Tanzeel\cookies.txt"
set "TESTURL=https://www.youtube.com/watch?v=aqz-KE-bpKQ"

echo [1/3] Checking for running %BROWSER% ...
powershell -NoProfile -Command "if (Get-Process -Name '%BROWSER%' -ErrorAction SilentlyContinue) { exit 1 } else { exit 0 }"
if errorlevel 1 (
    echo.
    echo   %BROWSER% is running. Close it completely and run this again,
    echo   otherwise the cookie database stays locked and the export fails.
    echo.
    exit /b 1
)

echo [2/3] Exporting cookies from %BROWSER% ...
python -m yt_dlp --cookies-from-browser %BROWSER% --cookies "%TARGET%" --skip-download --simulate "%TESTURL%" 1>nul 2>nul

if not exist "%TARGET%" (
    echo.
    echo   Export failed: no cookie file was written.
    exit /b 1
)

echo [3/3] Verifying ...
for /f %%C in ('python -c "import sys;p=r'%TARGET%';print(sum(1 for l in open(p,encoding='utf-8',errors='ignore') if l.strip() and not l.startswith('#')))"') do set "COUNT=%%C"

echo.
echo   Cookie file : %TARGET%
echo   Cookie count: !COUNT!
echo.
echo   The server now uses these cookies for extraction and downloads.
exit /b 0
