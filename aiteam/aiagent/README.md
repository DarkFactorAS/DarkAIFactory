# aiagent

Scaffold for the custom agent (DFAI-52). One image, two containers sharing the `aiagent_workspaces` volume:

- `aiagent-git-fetch` (`--git-fetch`): clones `AIAGENT_REPOSITORY_URL` (optional `AIAGENT_REPOSITORY_BRANCH`) into `/workspaces/repo`, writes a completion marker and exits.
- `aiagent-runner` (`--runner`): starts after the fetch succeeds, mounts the volume and serves `/health` and `/workspace` (whether the checkout is readable) on port 8080 inside the `aiagent` network.

Build: `dotnet build aiteam/aiagent/src/AiAgent/AiAgent.csproj`. Test: `dotnet test aiteam/aiagent/AiAgent.Tests.csproj`.
Run: `aiteam/docker-up.sh` / `aiteam/docker-down.sh` (aiagent services only; existing agents are untouched).
