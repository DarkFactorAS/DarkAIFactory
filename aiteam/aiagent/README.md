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

## Jira capability (`Capabilities/Jira`)

Copied from `aijiramanager`, the Jira parts of `aiworker`/`aiclaudeworker` and `PullRequestCloser` in
`aimanager` (nothing is shared). Runs only for agents whose `Jira` capability is enabled. Per cycle, scoped
to the agent's projects:

1. Intake: `agent_tasks` rows in `New` become Jira issues (setting `Jira.JiraIntakeEnabled`, default `true`).
   A startup warning is logged when intake is on, because `jira-manager-agent` does the same.
2. Pickup/status/recovery (`JiraWorkflow`, used by the Builder): `aiagent-picked`/`aiagent-failed`/
   `aiagent-ignored` labels from `workflow_labels[aiagent]`; unrecorded pull requests are recovered
   from the `aiagent-pr-tracking-pending` label.
3. In Review → Done when all `agent_pull_requests` rows of the issue are merged.
4. To Do/Backlog → Selected for Development when all "is blocked by" issues are Done; source statuses
   via `Jira.JiraUnblockSourceStatuses` (default `To Do,Backlog`).
