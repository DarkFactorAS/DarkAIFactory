#!/usr/bin/env bash
# Reset the Claude CLI login stored in the claude_config volume and log in again.
# Usage: ./login_claude.sh   (can be run from any directory)
set -euo pipefail

cd "$(dirname "$0")/aiteam"

if ! volume="$(docker compose config --format json | jq -er '.volumes.claude_config.name | strings | select(length > 0)')"; then
  printf 'Unable to resolve the claude_config volume name from Docker Compose configuration.\n' >&2
  exit 1
fi

# Stop and remove the runner so the volume is no longer in use.
docker compose rm --stop --force claude-cli
docker volume rm --force "$volume"

# Compose recreates the volume; log in and store the session there.
docker compose run --rm -it --entrypoint claude claude-cli auth login

docker compose up -d claude-cli
