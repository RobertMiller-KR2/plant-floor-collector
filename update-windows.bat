@echo off
setlocal
cd /d "%~dp0"
echo Rebuilding Plant Floor Collector image and restarting containers...
docker compose -f docker-compose.updatable.yml up -d --build
if errorlevel 1 (
  echo.
  echo Update failed.
  pause
  exit /b 1
)
echo.
echo Update complete. Open http://localhost:8080
pause
