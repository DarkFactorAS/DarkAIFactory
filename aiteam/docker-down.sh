#!/usr/bin/env bash
# Stops the aiagent containers only; the shared volume is kept.
set -euo pipefail
cd "$(dirname "$0")"
docker compose stop aiagent-runner aiagent-git-fetch
docker compose rm --force aiagent-runner aiagent-git-fetch
