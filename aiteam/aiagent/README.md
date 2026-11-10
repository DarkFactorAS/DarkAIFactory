# aiagent

Minimal .NET host for the custom agent (DFAI-52). Provides DI setup and a `/health` endpoint on port 8080.

- Project: `src/AiAgent/AiAgent.csproj`
- Config: `Config/appsettings.json` (override with `Config/appsettings.<Environment>.json`)
- Docker: `docker compose up aiagent` from `aiteam/`
