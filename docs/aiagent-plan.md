# aiAgent – technical plan

Status: **Draft for review** (2026-11-12). No Jira issues have been created yet.
Source: the original requirements (section 0) plus the decisions recorded in section 2.

---

## 0. Original requirements

The requirements below are the full original wording, with only spelling fixed. Each one has an
id (R1–R33) and a link to where the plan covers it.

### 0.1 How the work is planned and tracked

| Id | Requirement | Covered in |
|---|---|---|
| R1 | Build a NEW agent. | 1, 3 |
| R2 | Make a plan for how to implement this feature. | This document |
| R3 | Create a list of all the Jira tasks needed. Don't split the Jira tasks up too much. | 7 |
| R4 | The Jira tasks are only created after the requester has reviewed and approved them. | 7 (not created) |
| R5 | Every new database table starts with `agent_`. | 1 (hard rules), 3.4 |
| R6 | Create the 3 stages as 3 normal Jira tasks that block this one (the parent). | 7 |
| R7 | All other tasks inside a stage are sub-tasks of that stage's task. | 7 |
| R8 | Unclear requirements are settled in this task (the parent), not in the tasks it creates. | 2, 7 |
| R9 | Tasks move to "Selected for Development" automatically when the tasks blocking them are Done. | 2 (#22), 4.1 (Jira), 7 (release process) |

### 0.2 Stage 1 – instructions for the agent

| Id | Requirement | Covered in |
|---|---|---|
| R10 | Leave all old agents alone and make NO changes to them. | 1 (hard rules), 4.4 |
| R11 | The old agents are ClaudeWorker, JiraManagerAgent, ManagerAgent and CodingAgent. | 1 (hard rules), 2 (#1 adds ReviewWorker) |
| R12 | The new agent is called aiAgent and lives in the `aiteam` folder. | 3.3 (`aiteam/aiagent`) |
| R13 | The agent has the core logic to run in a Docker container. | 3.1 |
| R14 | The GitHub logic is also in this agent. | 2 (#5), 3.3 (`GitHub/`), 4.1 |
| R15 | Fetching the code happens in one container, and the AI work with the model happens in another. | 3, 3.1, 3.2 |

### 0.3 Stage 1 – capabilities

| Id | Requirement | Covered in |
|---|---|---|
| R16 | Each of the old agents' capabilities lives in its own folder under `Capabilities`. | 3.3 |
| R17 | The capability code is always in aiAgent. Each capability is switched on or off with a flag in a new table. | 3.4 (`agent_capabilities`), 4.4 |
| R18 | Capability: Jira handling and sync (all the current Jira logic). | 2 (#3), 4.1 |
| R19 | Capability: Planning logic. aimanager has this module for writing a good plan from the instructions in Jira; copy it. | 2 (#4), 4.1 |
| R20 | Capability: Builder (create, build and test code), with the tools needed to build. | 2 (#9), 3.1, 4.1 |
| – | (Added by decision 1) Capability: Review, copied from aireviewworker. | 2 (#1), 4.1 |

### 0.4 Stage 1 – language model

| Id | Requirement | Covered in |
|---|---|---|
| R21 | aiAgent has two modules, each in its own folder under `LanguageModel`. | 3.3 |
| R22 | The supported language models are GitHub Copilot and Claude. | 4.2 |
| R23 | aiAgent contains the code for both models. A setting in the database picks the active one. | 3.4 (`llm_provider`), 4.2 |
| R24 | Use a separate volume for `.claude-config`, as aiclaudeworker does. | 2 (#11), 3.1 |

### 0.5 Stage 2 – configure the agent

| Id | Requirement | Covered in |
|---|---|---|
| R25 | Configure the agent in a new table with these fields: name (free text), role (free text), the agent's generic task/behavior read from the database (free text), the language model it runs, and its enabled capabilities. | 3.4, 5.1 |
| R26 | Add a dashboard interface for configuring an agent, placed above the input box in the centre. | 5.2 |
| R27 | Show a slot for each agent with its info. | 5.2 |
| R28 | Make it possible to add a new agent (+). | 5.1, 5.2 |
| R29 | Make it possible to edit an agent. | 5.1, 5.2 |

### 0.6 Stage 3 – tracking agent usage

| Id | Requirement | Covered in |
|---|---|---|
| R30 | Track token usage (where possible). | 6.1 |
| R31 | Track how many tool calls it makes. | 6.1 |
| R32 | Track the time spent on a task, and the number of tasks that succeeded and failed. | 6.1, 6.3 |
| R33 | Show these numbers in the agent's box in the dashboard (if the agent is configured). | 6.2 |

---

## 1. Goal

Build one new, configurable agent, **aiAgent** (`aiteam/aiagent`). It can do
everything the old agents do, but each capability and language model is turned on
per agent in the database. Then add agent configuration and usage metrics to the
dashboard.

The work is split into three stages, each with its own Jira task:

1. **aiAgent core:** containers, GitHub logic, language models and the four capabilities.
2. **Agent configuration:** per-agent settings and the dashboard UI for them.
3. **Usage tracking:** token, tool-call, time and success/failure metrics, shown in the dashboard.

### Hard rules

- **Old agents are not changed.** Old agents means `aijiramanager`
  (JiraManagerAgent), `aimanager` (ManagerAgent / TechLead), `aiworker` (CodingAgent),
  `aiclaudeworker` (ClaudeWorker) and `aireviewworker` (ReviewWorker). No source
  changes are made in their folders, and none in `aiteam/shared`.
- These shared parts *are* changed, but only by adding things:
  - `dbmigration`: new tables and seed rows.
  - `aidashboard`: the new UI and endpoints.
  - `docker-compose.yml`: new services and volumes.
  - The repository scripts and docs.
- **Every new table starts with `agent_`.** The existing `agent_tasks` table is
  unrelated (it's the dashboard → Jira queue). Its schema is not changed.
- **Safety stays in code.** CLI permission flags, credential stripping and
  "never push / never use `gh`" are hard-coded and can't be changed from the
  database or the dashboard.

---

## 2. Decisions

| # | Topic | Decision |
|---|---|---|
| 1 | Review worker | `aireviewworker` is an old agent and isn't changed. Its logic is copied into aiAgent as a 4th capability, **Review**. |
| 2 | Old agents | They will be retired once aiAgent is proven, so copying their code is acceptable. |
| 3 | Jira capability | Covers all of: the JiraManager flow (`agent_tasks` → issue), worker pickup and status changes with recovery, closing merged work, and releasing blocked tasks. |
| 4 | Planning capability | TechLead's full triage (clarify, analysis_only, small, needs_approval, split) plus the plan fingerprint. |
| 5 | PR creation | Done by the core GitHub logic, not by a capability. |
| 6 | `aiteam/shared` | **Copied** into aiAgent. aiAgent doesn't reference the shared project. |
| 7 | Old scaffold | The leftover `aiteam/aiagent/**/bin,obj` is deleted. DFAI-53 and DFAI-63 are closed as superseded. |
| 8 | Running several agents | One orchestrator container runs one polling loop per enabled agent row, plus one shared runner container. |
| 9 | Runner toolchains | .NET SDK, Node.js and Python. **No Unity.** |
| 10 | Runner image | One runner with both CLIs, Claude Code and Copilot CLI. |
| 11 | Claude config | A **new** volume, `aiteam_aiagent_claude_config`, with its own login. |
| 12 | Running alongside old agents | Yes. aiAgent uses its own labels under its own client key (`aiagent`). |
| 13 | Language model field | Provider (`Claude` or `Copilot`) plus an optional model id. Empty means the default. |
| 14 | Behavior text | Added after the built-in prompt as an "Agent role and behavior" section. |
| 15 | Dashboard | Add, edit, enable/disable, delete and live status. All writes need `Dashboard__WriteToken`. |
| 16 | Agent scope | Each agent has a list of Jira project keys, mapped to repositories through `manager_projects`. |
| 17 | Config reload | Config is re-read every polling cycle. A run already in progress keeps its old config. |
| 18 | Success | Defined per capability (see 6.3). |
| 19 | Time | Total wall-clock time and LLM time are stored separately. |
| 20 | Metrics view | All-time and last-7-days totals, plus USD cost for Claude. Copilot shows N/A for tokens and tool calls. |
| 21 | Stage order | Chained: Stage 1 → Stage 2 → Stage 3. All three block the parent. |
| 22 | Releasing blocked tasks | Done by the aiAgent Jira capability. Stage 2 has to be moved by hand, because that code ships in Stage 1. |
| – | Claiming work | MariaDB `GET_LOCK` on the issue key (the pattern copied from `JiraIssueClaimRepository`) plus aiAgent's pickup label. Intake has no issue key yet, so it uses a separate `GET_LOCK` on the `agent_tasks` id (`aiagent-task-<id>`): take the lock, re-read the row and skip it unless it is still `New` with no Jira key, then create the issue and persist the Jira key and status while still holding the lock. |
| – | Branch and PR naming | aiAgent branches use `aiagent/<KEY>-…` (never `ai-agent/`), and PR titles start with `aiagent: <KEY> …` (never with the bare key or `[KEY]`). Neither matches the `PullRequestTrackingRecovery` patterns, so `aimanager` never imports aiAgent PRs into `pull_requests` for the old review worker. |
| – | Deleting an agent | Soft delete (`deleted_at`). Run history is kept, so all-time totals stay correct. |

---

## 3. Architecture

```
                     ┌──────────────────── aidashboard ─────────────────────┐
                     │ agent slots (+ / edit / enable / delete / metrics)   │
                     └───────────────┬──────────────────────────────────────┘
                                     │ agent_definitions, agent_capabilities,
                                     │ agent_projects, agent_status, agent_runs
                                ┌────▼─────┐
  Jira  ◄──────────────────────►│ MariaDB  │◄──── dbmigration (creates all agent_* tables)
   ▲                            └────▲─────┘
   │ Jira / GitHub credentials       │
┌──┴─────────────────────────────────┴─────┐  HTTP (private network)   ┌────────────────────────────┐
│ aiagent  (orchestrator)                  │──────────────────────────►│ aiagent-runner             │
│  AgentHost → one loop per enabled agent  │  /run, /health             │  Claude Code CLI + Copilot │
│  Capabilities: Jira, Planning,           │                            │  CLI, .NET, Node, Python   │
│    Builder, Review                       │◄──── result + usage ───────│  no DB, no Jira, no repo   │
│  GitHub core: clone, branch, commit,     │                            │  token                     │
│    push, PR                              │                            └──────▲─────────────────────┘
└──────────────┬───────────────────────────┘                                   │
               │ aiagent_workspaces volume (clone here, the runner edits/builds here)
               └───────────────────────────────────────────────────────────────┘
                                         aiteam_aiagent_claude_config → runner only
```

### 3.1 Containers (`docker-compose.yml`)

| Service | Image target | Volumes | Networks | Secrets it can see |
|---|---|---|---|---|
| `aiagent` | `aiteam/aiagent/Dockerfile` target `orchestrator` | `Config:ro`, `aiagent_workspaces` | default, `aiagent-runner` | DB, Jira, GitHub repository token, Copilot token (sent per request) |
| `aiagent-runner` | same Dockerfile, target `runner`, command `--runner-server` | `aiagent_workspaces`, `aiteam_aiagent_claude_config` | `aiagent-runner` only | Claude login (volume), read-only `NUGET_USERNAME` / `NUGET_TOKEN` for package restore. The Copilot token arrives per request and is never stored |

Both containers keep today's hardening: `cap_drop: ALL`, `no-new-privileges`,
`init: true`, `HOME=/tmp`, and a `/health` healthcheck on the runner.
`aiagent` depends on `dbmigration` (completed) and `aiagent-runner` (healthy).
`login_claude.sh` gets an option to log in to the new volume.

Splitting the work between the containers:
- **Orchestrator:** clones the repository, creates the branch, commits, pushes, opens
  the PR, and talks to Jira and the database.
- **Runner:** runs the LLM CLI, and the build and test commands, inside the workspace.
  Only the orchestrator has the repository token, so the runner can't push.

### 3.2 Runner protocol (copied from the `claude-cli` / `review-runner` pattern)

`POST /run` takes:

```json
{ "provider": "Claude|Copilot", "model": "…", "mode": "plan|implement|review",
  "workspace": "<dir under /workspaces>", "prompt": "…",
  "buildCommand": "…?", "testCommand": "…?", "copilotToken": "…?" }
```

It returns `{ exitCode, output, usage?, toolCalls?, llmDurationMs }`.

- **Workspace path:** the runner checks that it is under `/workspaces`.
- **Permissions:** the runner chooses the permission set from `mode`, using the
  hard-coded flags copied from the old runners:
  - `plan`: read only.
  - `implement` / `review`: edits allowed, `git push` and `gh` denied.
- **Prompt delivery:** the prompt goes in on stdin. Credentials are stripped from
  the environment just as in `ClaudeRunner.cs` and `CopilotRunner.cs`.
- **Package feed:** the runner receives read-only `NUGET_USERNAME` / `NUGET_TOKEN`
  (as the `claude-cli` service does today) and keeps them in the sanitized
  build/CLI environment, so `NuGet.config` can restore `DarkFactor.Common.Lib`
  packages. Repository-write, Jira and database credentials are still excluded.

### 3.3 Folder layout

```
aiteam/aiagent/
  Dockerfile                    # targets: orchestrator, runner
  README.md
  src/AiAgent/
    Program.cs                  # --runner-server switches to runner mode
    Core/                       # AgentHost, AgentLoop, AgentContext, config reload, claim lock, prompt composition
    GitHub/                     # clone (with submodules), branch, commit, push, PR, recorded in agent_pull_requests
    Capabilities/
      Jira/                     # task intake, pickup/status/recovery, close merged, release blocked
      Planning/                 # triage + plan + fingerprint (from aimanager)
      Builder/                  # implement + build/test (from aiworker / aiclaudeworker)
      Review/                   # PR review-comment fixing (from aireviewworker)
    LanguageModel/
      ILanguageModel.cs
      Claude/                   # Claude Code CLI args, stream-json parsing
      Copilot/                  # Copilot CLI args
    Runner/                     # runner HTTP server + client
    Providers/                  # Jira client, GitHub client
    Repositories/               # copied from shared + new agent_* repositories
    Metrics/                    # Stage 3: run recorder
  tests/AiAgent.Tests/
```

Each capability implements one interface:

```csharp
public interface IAgentCapability
{
    AgentCapabilityKind Kind { get; }                       // Jira, Planning, Builder, Review
    Task RunCycleAsync(AgentContext context, CancellationToken ct);
}
```

`AgentLoop` runs every polling cycle:
1. Reload the agent row.
2. If the agent is disabled or deleted, idle.
3. Otherwise run each enabled capability in a fixed order: Jira → Planning → Builder → Review.
4. **Stage 2 onwards only:** write a heartbeat to `agent_status`. Stage 1 has no
   `agent_status` table and skips this step.

### 3.4 Database (all tables created by `dbmigration`)

**Stage 1**

| Table | Key columns |
|---|---|
| `agent_definitions` | `id`, `name`, `role`, `behavior` TEXT, `llm_provider` ENUM('Claude','Copilot'), `llm_model` NULL, `enabled`, `created_at`, `updated_at`, `deleted_at` NULL |
| `agent_capabilities` | `agent_id`, `capability` ENUM('Jira','Planning','Builder','Review'), `enabled`. PK (`agent_id`, `capability`) |
| `agent_projects` | `agent_id`, `project_key` (must exist in `manager_projects`). PK (`agent_id`, `project_key`) |
| `agent_plans` | Same as `manager_plans`, plus `agent_id`. aiAgent's plans are kept apart from TechLead's |
| `agent_pull_requests` | Same as `pull_requests`, plus `agent_id` |
| `agent_review_attempts` | Same as `review_comment_attempts`, plus `agent_id` |

Stage 1 also seeds:
- **One default agent:** "aiAgent", provider Claude, all capabilities off. It's
  edited in Adminer until Stage 2.
- **`settings` rows** for client key `aiagent`. These are blank credentials, filled
  in the same way as for the existing agents.
- **`workflow_labels` rows** for client key `aiagent`, for example `aiagent-picked`,
  `aiagent-failed`, `aiagent-analyzed`, `aiagent-planned` and `aiagent-ignored`.

**Stage 2**

| Table | Key columns |
|---|---|
| `agent_status` | `agent_id` PK, `state` ENUM('Idle','Running','Error','Disabled'), `current_issue_key` NULL, `current_capability` NULL, `heartbeat_at` |

**Stage 3**

| Table | Key columns |
|---|---|
| `agent_runs` | `id`, `agent_id`, `capability`, `issue_key`, `provider`, `model`, `started_at`, `finished_at`, `llm_duration_ms`, `outcome` ENUM('Succeeded','Failed'), `failure_reason`, `input_tokens` NULL, `output_tokens` NULL, `cache_read_tokens` NULL, `cache_write_tokens` NULL, `cost_usd` DECIMAL NULL, `tool_calls` NULL, `num_turns` NULL. Index (`agent_id`, `started_at`) |

NULL means "not available" (Copilot), which the dashboard shows as N/A.

---

## 4. Stage 1 – aiAgent core

### 4.1 Capabilities (copied, never shared)

| Capability | Copied from | Notes |
|---|---|---|
| **Jira** | `aijiramanager`, the Jira parts of `aiworker` / `aiclaudeworker`, and `PullRequestCloser` in `aimanager` | 1) Intake: `agent_tasks` rows in `New` become Jira issues, scoped to the agent's projects. 2) Pickup, status changes and recovery with the `aiagent-*` labels. 3) Move `In Review` → `Done` when every row in `agent_pull_requests` for the issue is merged. 4) **New:** move an issue to `Selected for Development` when every issue linked to it by "is blocked by" is `Done`. The source statuses are configurable, default `To Do` / `Backlog`. |
| **Planning** | `aimanager` (`ManagerPlanner`, `TriageResult`, prompts) | Same five decisions and split logic. Writes `agent_plans` and labels the issue `aiagent-analyzed`. Runs the runner in `plan` mode. |
| **Builder** | `aiworker` / `aiclaudeworker` (`Program.cs`, `PromptBuilder.cs`) | Plan gate (the fingerprint must match), then claim, branch, run in `implement` mode, run build and test, then the core opens the PR. The fallback local plan stays. |
| **Review** | `aireviewworker` | Unresolved review comments on aiAgent's own PRs (`agent_pull_requests`). Runs in `review` mode. Only the cited files may change. Build and test run, then a push that is never forced. |

### 4.2 Language models

- `ILanguageModel` builds the CLI arguments and parses the output for one provider.
- **Claude:** `claude -p`. The model comes from the agent row, otherwise the default `claude-sonnet-5-5`. The permission flags are copied from `ClaudeRunner.cs`.
- **Copilot:** `copilot -p`. The model comes from the agent row, otherwise the default `gpt-5-mini`. The permission flags are copied from `CopilotRunner.cs`.
- The provider is chosen per agent from `agent_definitions.llm_provider`.

### 4.3 Coexistence with the old agents

aiAgent uses its own labels and tables, so the old workers ignore its issues and it
ignores theirs. Labels and tables alone don't isolate PRs: `PullRequestTrackingRecovery`
imports open PRs for `In Review` issues by branch (`ai-agent/<KEY>`) or title
(`<KEY>` / `[<KEY>]` prefix) without checking labels. aiAgent therefore uses its own
branch and title namespace (`aiagent/<KEY>-…`, `aiagent: <KEY> …`, see section 2) so
neither pattern matches. **Two overlaps remain, and they're handled by configuration:**

1. **The `agent_tasks` intake.** The old `jira-manager-agent` also processes
   `New` rows. Turn on aiAgent's intake (the first part of the Jira capability) only
   when `jira-manager-agent` is stopped. aiAgent logs a warning at startup when its
   intake is on.
2. **The TechLead's projects.** `aimanager` plans every issue in
   `Selected for Development` in its mapped projects. An agent with Planning turned
   on shouldn't share projects with a running `manager-agent`. Every aiAgent project
   must exist in `manager_projects`, and the manager loads and polls every row in
   that table, so an aiAgent-only project is also polled by TechLead; `aiagent-*`
   labels don't exclude it. Before turning on aiAgent Planning, either set the
   manager's `Jira.JiraJql` to exclude the aiAgent-owned projects/issues, or stop
   `manager-agent`. The README documents this.

### 4.4 Acceptance criteria

- `docker compose up` starts `aiagent` and `aiagent-runner`. The runner is healthy, and the old services are unchanged.
- With only Builder turned on for a test project, a planned issue ends as a PR plus a Jira `In Review` status, using `aiagent-*` labels.
- Each capability can be switched on or off in `agent_capabilities` without a restart.
- Switching `llm_provider` between Claude and Copilot works without a restart.
- All test projects pass, and the new `AiAgent.Tests` covers the capabilities, prompt composition, the runner and the GitHub core.
- `git diff` shows no changes under the old agents' folders or `aiteam/shared`.

---

## 5. Stage 2 – agent configuration

### 5.1 Database and API

- **`dbmigration`:** adds `agent_status`.
- **New endpoints in `aidashboard/Controllers/AgentEndpoints.cs`:**
  - `GET /api/agents`
  - `GET /api/agents/{id}`
  - `POST /api/agents`
  - `PUT /api/agents/{id}`
  - `PATCH /api/agents/{id}/enabled`
  - `DELETE /api/agents/{id}` (soft delete)
- **Auth:** every write requires `Authorization: Bearer <Dashboard__WriteToken>`.
- **Validation:**
  - `name` is required and unique among agents that aren't deleted.
  - `behavior` can be at most 8,000 characters.
  - `llm_provider` must be a known value.
  - Project keys must exist in `manager_projects`.

### 5.2 UI (`aidashboard/js`)

- **`AgentSlots.jsx`:** sits in the centre column **above** `TaskEditor` (the input
  box). It shows one card per agent and a **+** card.
- **Each card shows:**
  - the name, role, provider and model
  - the enabled capabilities as chips
  - the projects
  - live status from `agent_status`: Idle, Running on `KEY-123`, Error, or Disabled. If the heartbeat is more than 3 polling intervals old, the card shows "Offline".
  - an enable toggle, plus edit and delete buttons. Delete asks for confirmation in the page.
- **`AgentEditor.jsx`:** a form for create and edit with these fields:
  - name
  - role
  - behavior (multi-line)
  - provider and model
  - capability checkboxes
  - project multi-select
- **Write token:** entered once per browser session, the same way the existing
  comment flow handles it.

### 5.3 aiAgent changes

- **Each cycle:** `AgentHost` reloads the agent rows. It starts loops for new
  agents and stops loops for agents that are disabled or deleted. A run in progress
  is allowed to finish.
- **Behavior text:** added after the built-in prompt as
  `## Agent role and behavior\nRole: …\n…`, inside a block marked as configuration
  data. It can't change the permission flags.
- **Live status:** `agent_status` is updated when a run starts and ends, and on
  every heartbeat.

### 5.4 Acceptance criteria

- An agent created in the dashboard starts running within one polling cycle, with no restart.
- An agent edited in the dashboard uses its new config from the next cycle.
- Writes without the token return `401`.
- Disabling an agent stops it picking up new work. Deleting it hides it but keeps its rows.

---

## 6. Stage 3 – usage tracking

### 6.1 Capture

- **One `agent_runs` row per unit of work:** one issue planned, one issue built,
  one PR reviewed, or one Jira action. The row is written when the run starts and
  finished when it ends, including on exceptions. Pure polling cycles with no work
  are **not** recorded.
- **Claude:** switch the runner to `--output-format stream-json --verbose`.
  - Count `tool_use` blocks to get `tool_calls`.
  - The final `result` event gives the input, output and cache tokens, `total_cost_usd`, `num_turns` and `duration_ms` (used as LLM time).
  - The runner still returns the plain text result to the capabilities.
- **Copilot:** record the LLM time from the process run time. Tokens, tool calls and cost stay NULL unless the CLI exposes usage. This is checked in Stage 3 and documented.
- **Wall-clock time:** `finished_at - started_at`, from claim to PR / comment / failure.

### 6.2 Dashboard

- **`GET /api/agents/metrics`** returns, per agent, aggregates for all time and for
  the last 7 days:
  - tasks succeeded and failed
  - total and average wall-clock time
  - total and average LLM time
  - input and output tokens
  - tool calls
  - cost in USD
- **Agent card:** shows a compact metrics row with two toggles, All-time / 7 days.
  Hovering shows the details. Values are `N/A` where the provider doesn't report them.

### 6.3 Success per capability

| Capability | Succeeded | Failed |
|---|---|---|
| Jira | The Jira action completed (issue created, status moved, issue released or closed) | An exception, or intake reached the 3-failure limit |
| Planning | The triage decision was posted to Jira and saved in `agent_plans` | An exception, an invalid triage JSON, or a failed post |
| Builder | The PR was opened and the tests passed | `aiagent-failed` label, tests failed, or nothing to commit |
| Review | The fix was pushed | The fix was discarded (out-of-scope files, build or test failed), or the attempt limit was reached |

### 6.4 Acceptance criteria

- After one Claude Builder run, the card shows 1 succeeded, plus non-zero tokens, tool calls, time and cost.
- A Copilot run shows its time and N/A for tokens and tool calls.
- The 7-day totals exclude runs older than 7 days.

---

## 7. Proposed Jira tasks (for approval – not created)

Structure: the parent is blocked by Stages 1, 2 and 3. Stage 2 is blocked by
Stage 1, and Stage 3 is blocked by Stage 2. The other items are sub-tasks of their
stage. All decisions are made in the parent, so the stage tasks hold no open
requirements.

**Release process:** the aiAgent Jira capability, which ships in Stage 1, moves a
blocked task to `Selected for Development` once its blockers are `Done`.
- **Stage 1:** move by hand when you approve the plan.
- **Stage 2:** move by hand when Stage 1 is Done, because the capability isn't running yet.
- **Stage 3, and the parent:** released by aiAgent automatically, if an aiAgent with the Jira capability is set up for the DFAI project.

**Stage integration gate:** the sub-task PRs merge into the stage branch
(`ai-agent/<STAGE-KEY>`). The manager's automatic stage-to-default PR needs a
stored split state that this manual process doesn't create, so for each stage:
open an explicit integration PR from the stage branch to the default branch once
all sub-task PRs are merged, and mark the stage `Done` only after that PR is
merged. The next stage is not released until then.

### Parent – aiAgent: new configurable agent (analysis and decisions)
This plan, and the decisions in section 2.

### Stage 1 – aiAgent core (Task, blocks the parent and Stage 2)
| # | Sub-task | Contents |
|---|---|---|
| 1.1 | Scaffold aiAgent and containers | Delete the leftover `bin/obj`. Add the project, the tests project, a Dockerfile with `orchestrator` and `runner` targets (.NET, Node, Python, Claude Code CLI, Copilot CLI), the compose services, the `aiagent_workspaces` and `aiteam_aiagent_claude_config` volumes, the `aiagent-runner` network, `login_claude.sh` support, and a README. Copy `shared` into aiAgent. |
| 1.2 | Database tables and seed data | `agent_definitions`, `agent_capabilities`, `agent_projects`, `agent_plans`, `agent_pull_requests`, `agent_review_attempts`. Seed the default agent, the `aiagent` settings and the `aiagent-*` workflow labels. Migration tests. |
| 1.3 | Core, GitHub logic and the runner | AgentHost with one loop per agent and per-cycle reload, the claim lock, the GitHub core (clone, branch, commit, push, PR), the runner HTTP server and client, and both language-model modules (Claude, Copilot) with hard-coded permission sets. |
| 1.4 | Jira capability | Intake, pickup/status/recovery, close merged work, release blocked tasks, and the coexistence warning. |
| 1.5 | Planning capability | Triage with its five decisions, splitting, `agent_plans` with the fingerprint, and the Jira comments. |
| 1.6 | Builder capability | Plan gate, implementation, build and test, then the PR through the core, and failure recovery. |
| 1.7 | Review capability | Fixing review comments on aiAgent PRs, the attempt limit, and the file-scope check. |

### Stage 2 – Agent configuration (Task, blocked by Stage 1, blocks Stage 3 and the parent)
| # | Sub-task | Contents |
|---|---|---|
| 2.1 | Agent API and `agent_status` | Migration, CRUD and enable endpoints, write-token auth, validation, and tests. |
| 2.2 | Dashboard agent slots | `AgentSlots` above `TaskEditor`, the + card, `AgentEditor`, enable/disable, delete, and live status. |
| 2.3 | aiAgent uses the per-agent config | Start and stop loops when agents change, add the behavior text to the prompt, scope work to the agent's projects, and write the status heartbeat. |

### Stage 3 – Usage tracking (Task, blocked by Stage 2, blocks the parent)
| # | Sub-task | Contents |
|---|---|---|
| 3.1 | Record agent runs | `agent_runs` migration, the run recorder in every capability, the Claude `stream-json` parser (tokens, tool calls, cost, LLM time), Copilot best effort, and the success rules from 6.3. |
| 3.2 | Show metrics in the dashboard | The `/api/agents/metrics` aggregates (all time and 7 days) and the metrics row on the agent card. |

**Housekeeping, not new tasks:** close DFAI-53 and DFAI-63 as superseded by Stage 1.

---

## 8. Risks

| Risk | Mitigation |
|---|---|
| The copied code drifts from the old agents while both run | Accepted, because the old agents will be retired (decision 2). Bug fixes made to an old agent must also be applied to aiAgent until then. |
| aiAgent and the old agents act on the same issue | Separate labels and tables, intake only while `jira-manager-agent` is stopped, and no shared Planning projects with `manager-agent` (4.3). |
| The behavior text is used to weaken the agent | It's only added after the built-in prompt. Permission flags and credential stripping stay in code. Dashboard writes need the write token. |
| Copilot CLI reports no usage | Those fields are nullable and shown as N/A. |
| The runner image gets large (two CLIs and three toolchains) | A multi-stage build, and a shared base layer with the orchestrator. |
| Parallel agent loops overload the runner | A configurable maximum number of concurrent runner jobs (default 2). Extra jobs wait for the next cycle. |

---

## 9. Readiness for an aiclaudeworker (checked on DFAI-151)

Checked against `aiteam/aiclaudeworker/README.md` and `aiteam/aimanager/README.md`. Stage 1 sub-tasks are
implemented by the existing worker, so the following must hold when the tasks are created.

| Check | Result / required action |
|---|---|
| Parent is not an epic | "New Agent Stage 1" is a normal **Task** (the worker skips epics). Sub-tasks 1.1–1.7 are Jira sub-tasks, so the worker branches them off `ai-agent/<STAGE-1-KEY>`. |
| Title | The Stage 1 task is named exactly **New Agent Stage 1** (Stages 2 and 3: "New Agent Stage 2" / "New Agent Stage 3"). |
| Blockers | The worker skips issues with an open "is blocked by" link. Chain sub-tasks: 1.2 and 1.1 first, then 1.3, then 1.4–1.7 each blocked by 1.3. Stage 2 is blocked by Stage 1, Stage 3 by Stage 2. |
| Stage integration | Sub-task PRs merge into `ai-agent/<STAGE-KEY>`, not the default branch, and the manager's automatic stage-to-default PR needs a stored split state that this manual process doesn't create. For each stage, once all sub-task PRs are merged, open an explicit integration PR from `ai-agent/<STAGE-KEY>` to the default branch. Mark the stage `Done` only after that PR is merged, so the next stage (blocked by it) starts from code that is already on the default branch. |
| Manager gate | The worker only picks up issues with the manager's `ai-analyzed` label and a current stored plan. The manager must plan each sub-task first. Each sub-task description must therefore be self-contained: files or projects touched, acceptance criteria, and the test commands. |
| Approval gates | The manager adds `ai-awaiting-approval` to tasks touching the database, architecture, Docker or deployment (1.1, 1.2, 1.3, 2.1, 3.1). Expect to approve these with `ai-approved`. Say so in those descriptions. |
| Repo mapping | The DFAI project needs a `manager_projects` row whose repository the worker token can reach. |
| Sizing | 1.3 (core, GitHub logic, runner and both language models) is too big for one PR. Split into 1.3a (core, `AgentHost`, claim lock, GitHub core), 1.3b (runner server/client) and 1.3c (Claude and Copilot language-model modules) before creating the tasks. |
| Validation | Every task must finish with `dotnet test` on all four existing test projects, plus `AiAgent.Tests` once 1.1 exists. |
| Wiring tasks | Cover explicitly: `WorkflowLabelPatcher.cs` and migrations (1.2), `aiteam/docker-compose.yml`, `.github/workflows` (CI and deploy entries for aiagent, add to 1.1), `aiteam/config/README.md`, the root `README.md` and `AGENTS_OVERVIEW.md` (1.1). |
| Hard rule | No task changes the old agents or `aiteam/shared` (see section 1). |

### Order of Jira actions on DFAI-151
1. Plan and readiness checked (this section).
2. Add the label `ai-awaiting-approval` to DFAI-151.
3. Do **not** create the stage tasks until `ai-approved` is present.
4. After approval, create "New Agent Stage 1" with sub-tasks, blockers and project/repo mapping as above.
5. When all of a stage's sub-task PRs are merged, open the stage integration PR to the default branch. Mark the stage `Done` only after that PR is merged.
