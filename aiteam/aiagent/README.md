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

Claude Code login for the runner: `./login_claude.sh` (Windows: `login_claude.cmd`).

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
runner in `plan` mode and applies one of the six triage decisions: `clarify` (questions as Jira comment and
the clarification label), `analysis_only` (analysis comment, transition to the analysis status), `small`,
`needs_approval` (approval label until a human adds the approved label), `split` (full plan proposed on the
parent, sub-tasks created with dependency links after approval, at most 10) and `create_tasks` (see below).

- `create_tasks`: the title or description explicitly asks for Jira tasks to be created. Up to 20 top-level
  issues of `Jira.JiraTaskIssueType` (default `Task`) are created in the same project, in dependency order with
  "Blocks" links; no approval is needed. Each task carries an `aiagent-task-correlation-<id>` label and its key is
  checkpointed in `agent_plans` right after creation, so a failed run resumes (after removing `aiagent-failed`)
  without duplicates. The new tasks stay in their initial status until a human moves them to Selected for
  Development; the source issue gets a comment listing them and is moved to `Jira.JiraDoneStatus`.

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
target the parent's `aiagent/parent/<KEY>` branch), runs the runner in `implement` mode (with build/test commands
from `AIAGENT_BUILD_COMMAND` / `AIAGENT_TEST_COMMAND`), pushes, opens the PR through `GitHubCore`
(recorded in `agent_pull_requests`) and moves the issue to the In Review status. Failures add
`aiagent-failed` and a recovery comment. With a custom `Jira.JiraJql` and no stored plan, a local plan is
generated (fallback). Settings are read from the `aiagent` client and the `aiagent` workflow labels.

## Review capability (`Capabilities/Review`)

Copied from `aireviewworker`. Registered only when `GitHub.GitHubRepositoryToken` is set. For each open pull
request in `agent_pull_requests` it collects unresolved, current inline comments by `AIAGENT_REVIEWER_LOGIN`
(default `copilot-pull-request-reviewer`) that are not handled yet, clones the head branch and fixes the comments
**one at a time**: one runner call in `review` mode with only that comment (build/test run inside the runner),
a check that only the comment's file changed, then a separate commit and push (never forced). The commit subject
says what was fixed and where (`DFAI-166: The HttpClient is never disposed (src/Worker/Client.cs:42)`); the body
quotes the comment. A failing comment is reset and the next one is tried; a failed push stops the remaining
comments until the next cycle.

- Attempts are recorded in `agent_review_attempts` (at most 3 per comment). A runner call that exceeds
  `RunnerTimeoutMinutes` (appsettings, default `30`) counts as a failed attempt; an agent shutdown or a busy
  runner does not.
- If the runner makes no changes for a comment, that is not an error: the comment is recorded as `Resolved` and
  its GitHub thread is left open for a human.
- After a fix is pushed the review thread is resolved (GraphQL `resolveReviewThread`) once all of its comments
  were pushed in that run; threads with a failed, unchanged or retried comment stay open. If resolving fails
  (for example missing permission) only a warning is logged. The token therefore needs Contents: write and
  Pull requests: write.
- Each scan logs how many comments are actionable, already handled and marked failed (warning), and which other
  reviewers were ignored. When a pull request has no unresolved review threads (outdated ones included) and no
  fix failed, `... is ready to be merged` is logged.
