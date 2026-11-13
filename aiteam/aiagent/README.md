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

## Planning capability

`src/AiAgent/Capabilities/Planning` is copied from aimanager (`ManagerPlanner`, `TriageResult`, prompts);
aimanager itself is unchanged. For every Selected-for-Development issue of the agent's projects it runs the
runner in `plan` mode and applies one of the five triage decisions: `clarify` (questions as Jira comment and
the clarification label), `analysis_only` (analysis comment, transition to the analysis status), `small`,
`needs_approval` (approval label until a human adds the approved label) and `split` (full plan proposed on the
parent, sub-tasks created with dependency links after approval, at most 10).

- The plan is stored in `agent_plans` together with the requirements fingerprint (`requirements_hash`:
  SHA-256 of summary, description and repository URL). A stored plan is reused until the title or
  description changes.
- After the plan is posted as Jira comments the issue gets the `aiagent-planned` and `aiagent-analyzed`
  labels (workflow_labels `PlannedLabel`/`AnalyzedLabel`); the Builder only takes issues with `aiagent-analyzed`.
  Issues with the `aiagent-picked`, `aiagent-failed` or `aiagent-ignored` label are not planned. A planning
  failure adds `aiagent-failed` and a Jira comment.
- `ClarificationLabel`, `ApprovalLabel` and `ApprovedLabel` are read from workflow_labels for `aiagent` when
  present and default to `aiagent-needs-clarification`, `aiagent-awaiting-approval` and `aiagent-approved`.

> **Warning:** Planning must not share projects with a running manager-agent. aimanager plans every
> Selected-for-Development issue of its mapped projects regardless of the `aiagent-*` labels, so both would plan
> the same issues. Either set the manager `Jira.JiraJql` to exclude the aiAgent projects, or stop manager-agent.

## Builder capability (`Capabilities/Builder`)

Copied from `aiclaudeworker` (Program.cs flow, PromptBuilder). For each assigned project key it picks
planned issues (`aiagent-analyzed`, not `aiagent-picked`), requires a posted stored plan in `agent_plans`
whose `requirements_hash` matches the current issue, claims the issue, branches (sub-tasks branch off and
target the parent's `ai-agent/<KEY>` branch), runs the runner in `implement` mode (with build/test commands
from `AIAGENT_BUILD_COMMAND` / `AIAGENT_TEST_COMMAND`), pushes, opens the PR through `GitHubCore`
(recorded in `agent_pull_requests`) and moves the issue to the In Review status. Failures add
`aiagent-failed` and a recovery comment. With a custom `Jira.JiraJql` and no stored plan, a local plan is
generated (fallback). Settings are read from the `aiagent` client and the `aiagent` workflow labels.
