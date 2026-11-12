@echo off
REM Reset the Claude CLI login stored in the claude_config volume and log in again.
REM Usage: login_claude.cmd   (can be run from any directory)
setlocal enabledelayedexpansion

cd /d "%~dp0aiteam" || exit /b 1

REM Resolve the volume name from the Compose configuration (PowerShell parses the JSON, so jq is not required).
set "volume="
for /f "usebackq delims=" %%v in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; $name = (docker compose config --format json | Out-String | ConvertFrom-Json).volumes.claude_config.name; if ($name -is [string] -and $name.Length -gt 0) { $name }"`) do set "volume=%%v"
if "!volume!"=="" (
  echo Unable to resolve the claude_config volume name from Docker Compose configuration. 1>&2
  exit /b 1
)

REM Stop and remove the runner so the volume is no longer in use.
docker compose rm --stop --force claude-cli || exit /b 1
docker volume rm --force "!volume!" || exit /b 1

REM Compose recreates the volume; log in and store the session there.
docker compose run --rm -it --entrypoint claude claude-cli auth login || exit /b 1

docker compose up -d claude-cli || exit /b 1

endlocal
