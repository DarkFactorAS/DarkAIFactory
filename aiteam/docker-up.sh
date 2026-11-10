#!/usr/bin/env bash
# Starts the aiagent containers (git-fetch + runner). Existing agents are managed separately.
set -euo pipefail
cd "$(dirname "$0")"
docker compose up --build --detach aiagent-git-fetch aiagent-runner
