@echo off
setlocal
cd /d "%~dp0"
echo Building and starting Plant Floor Collector...
docker compose -f docker-compose.updatable.yml up -d --build
if errorlevel 1 (
  echo.
  echo Failed to start Plant Floor Collector.
  pause
  exit /b 1
)
echo.
echo Plant Floor Collector is starting.
echo Open http://localhost:8080
start http://localhost:8080
pause
