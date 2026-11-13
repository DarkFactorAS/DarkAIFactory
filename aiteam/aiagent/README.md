# AiAgent (aiagent)

Scaffold for the new agent (DFAI-154, stage 1). The existing agents are unchanged.

## Layout

- `src/AiAgent` – `Program.cs` (orchestrator; `--runner-server` starts the runner) and the
  folders `Core`, `GitHub`, `Capabilities`, `LanguageModel`, `Runner`, `Providers`,
  `Repositories`, `Metrics`. `Core` and `Repositories` hold code copied from `aiteam/shared`
  (namespaces `AiAgent.Core` / `AiAgent.Repositories`); `aiteam/shared` itself is not modified.
- `tests/AiAgent.Tests` – xUnit tests: `dotnet test aiteam/aiagent/tests/AiAgent.Tests/AiAgent.Tests.csproj`.
- `Dockerfile` – multi-stage, shared `base` layer (.NET SDK, Node, Python, Claude Code CLI,
  Copilot CLI) with targets `orchestrator` and `runner`.

## Compose

- `aiagent` – orchestrator; mounts `/app/Config` and `aiagent_workspaces`; starts after
  `dbmigration` completed and `aiagent-runner` is healthy.
- `aiagent-runner` – runs `--runner-server` (`/health` on 8080); mounts `aiagent_workspaces` and
  `aiteam_aiagent_claude_config`, no `/app/Config`, only `NUGET_USERNAME`/`NUGET_TOKEN` (read-only
  package access); on the `aiagent-runner` network.
- Both: `cap_drop: ALL`, `no-new-privileges`, `init: true`, `HOME=/tmp`.

Claude Code login for the runner: `./login_claude.sh --aiagent`.

Configuration is read from `aiteam/config/appsettings.<Environment>.json` (`PollIntervalSeconds`).
CI: `.github/workflows/aiagent-ci-*.yml`; deploy: job `aiagent` in `aiteam-deploy.yml`.

## Builder capability (`Capabilities/Builder`)

Copied from `aiclaudeworker` (Program.cs flow, PromptBuilder). For each assigned project key it picks
planned issues (`aiagent-analyzed`, not `aiagent-picked`), requires a posted stored plan in `agent_plans`
whose `requirements_hash` matches the current issue, claims the issue, branches (sub-tasks branch off and
target the parent's `ai-agent/<KEY>` branch), runs the runner in `implement` mode (with build/test commands
from `AIAGENT_BUILD_COMMAND` / `AIAGENT_TEST_COMMAND`), pushes, opens the PR through `GitHubCore`
(recorded in `agent_pull_requests`) and moves the issue to the In Review status. Failures add
`aiagent-failed` and a recovery comment. With a custom `Jira.JiraJql` and no stored plan, a local plan is
generated (fallback). Settings are read from the `aiagent` client and the `aiagent` workflow labels.
