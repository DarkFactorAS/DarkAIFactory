@echo off
REM Reset the Claude CLI login stored in a Docker volume and log in again.
REM Usage: login_claude.cmd [--aiagent]   (can be run from any directory)
REM   (default)   claude_config volume, used by claude-cli
REM   --aiagent   aiteam_aiagent_claude_config volume, used by aiagent-runner
setlocal enabledelayedexpansion

if "%~1"=="" (
  set "volume_key=claude_config"
  set "service=claude-cli"
) else if /i "%~1"=="--aiagent" (
  if not "%~2"=="" goto usage
  set "volume_key=aiteam_aiagent_claude_config"
  set "service=aiagent-runner"
) else (
  goto usage
)

cd /d "%~dp0aiteam" || exit /b 1

REM Resolve the volume name from the Compose configuration (PowerShell parses the JSON, so jq is not required).
set "volume="
for /f "usebackq delims=" %%v in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; $name = (docker compose config --format json | Out-String | ConvertFrom-Json).volumes.'!volume_key!'.name; if ($name -is [string] -and $name.Length -gt 0) { $name }"`) do set "volume=%%v"
if "!volume!"=="" (
  echo Unable to resolve the !volume_key! volume name from Docker Compose configuration. 1>&2
  exit /b 1
)

REM Stop and remove the runner so the volume is no longer in use.
docker compose rm --stop --force !service! || exit /b 1
docker volume rm --force "!volume!" || exit /b 1

REM Compose recreates the volume; log in and store the session there.
docker compose run --rm -it --entrypoint claude !service! auth login || exit /b 1

docker compose up -d !service! || exit /b 1

endlocal
exit /b 0

:usage
echo Usage: %~nx0 [--aiagent] 1>&2
exit /b 2
