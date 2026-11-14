#!/usr/bin/env bash
# Reset the Claude CLI login of aiagent-runner (aiteam_aiagent_claude_config volume) and log in again.
# Usage: ./login_claude.sh [--aiagent]   (can be run from any directory; --aiagent is accepted for compatibility)
set -euo pipefail

cd "$(dirname "$0")/aiteam"

if [[ $# -gt 1 || ( $# -eq 1 && "$1" != "--aiagent" ) ]]; then
  printf 'Usage: %s [--aiagent]\n' "$0" >&2
  exit 2
fi

volume_key=aiteam_aiagent_claude_config
service=aiagent-runner

if ! volume="$(docker compose config --format json | jq -er --arg key "$volume_key" '.volumes[$key].name | strings | select(length > 0)')"; then
  printf 'Unable to resolve the %s volume name from Docker Compose configuration.\n' "$volume_key" >&2
  exit 1
fi

# Stop and remove the runner so the volume is no longer in use.
docker compose rm --stop --force "$service"
docker volume rm --force "$volume"

# Compose recreates the volume; log in and store the session there.
docker compose run --rm -it --entrypoint claude "$service" auth login

docker compose up -d "$service"
