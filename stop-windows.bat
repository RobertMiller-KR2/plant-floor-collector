@echo off
setlocal
cd /d "%~dp0"
docker compose -f docker-compose.updatable.yml down
pause
