docker compose down claude-cli
docker volume rm aiteam_claude_config
docker volume create claude_config
docker compose up -d claude-cli
docker compose run --rm -it --entrypoint claude claude-cli auth login