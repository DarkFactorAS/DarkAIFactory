# AITechLead (Jira Manager Agent)

AITechLead (folder and DB client key still `aimanager`) polls Jira, clones each mapped repository for read-only context, asks
Copilot CLI to triage each ticket (clarify, analysis only, small, needs approval or split into
sub-tasks), saves the full plan in MariaDB and posts it to Jira. It does not implement code,
run builds/tests in the cloned repository, create branches, commit, push or open pull
requests. It changes ticket statuses for analysis-only tickets (to `Code Review`), for
released sub-tasks (to `In Progress`), and for `In Review` tickets whose pull requests
are all merged (to `Done`); see [Triage behaviour](#triage-behaviour) and
[Closing merged pull requests](#closing-merged-pull-requests).
Copilot shell and write tools are always disabled.

## Database

The standalone [dbmigration](../dbmigration/README.md) executable creates:

- `manager_projects`: auto-increment `BIGINT` primary key `id`, unique
  `jira_project_key`, and `target_repository_url`.
- `manager_plans`: auto-increment `BIGINT` primary key `id`, `manager_project_id`
  foreign key, unique `jira_key`, full `plan` (`LONGTEXT`), `posted_to_jira`
  flag, `created` timestamp, `requirements_hash` and `source_updated` metadata.
- `pull_requests`: written by aiworker for every pull request it opens (`jira_key`,
  `repository_url`, `pr_number`, unique `pr_url`, `created`). A Jira issue can have several.

The manager does not create tables or run migrations. Run dbmigration first;
missing tables or database read failures stop startup.
Copy `aiteam/config/appsettings.Production.json.example`
to `aiteam/config/appsettings.Production.json` and set
`DatabaseConnection` to match MariaDB credentials in `aiteam/.env`.
The runtime configuration is ignored by Git. Compose mounts this directory
read-only at `/app/Config` for all services; override `AITEAM_CONFIG_PATH` for another directory.
For local runs, configured JSON files are copied into the executable's `Config`
directory. See [App configuration](../config/README.md) for Production,
Development and Acceptance setup. Startup selects the executable directory
as its working directory. Jira/GitHub settings load from the shared `settings`
table, with three client-specific rows; see the configuration guide for migration.

Populate the mappings after running dbmigration:

```sql
INSERT INTO manager_projects (jira_project_key, target_repository_url)
VALUES
    ('DFAI', 'https://github.com/owner/first-repository.git'),
    ('BOTS', 'https://github.com/owner/second-repository.git');
```

Mappings reload in ID order every polling cycle. Each Jira project maps to one
HTTPS GitHub repository; multiple projects may share a repository. Empty mappings
are logged and the manager waits. Invalid mappings or database read failures
stop the manager. A Jira polling failure does not prevent polling other projects.
Polling logs announce each distinct project mapping only once per process,
including newly added or changed mappings. The empty-project waiting message
is also printed only once. Polling continues every cycle and errors are still reported.
Both agents read project keys and repository URLs from `manager_projects`.
The worker reloads the same mappings each polling cycle; only when the table is
empty does it use shared `Jira.JiraProjectKey` and `GitHub.TargetRepositoryUrl`.

## Planning and recovery

For each eligible ticket, the manager creates a temporary repository checkout,
generates a plan without changing files and removes the checkout. It saves the
plan in `manager_plans` **before** posting to Jira, marks it as posted after all
comments succeed, then adds the configured completion label
(`ai-manager-planned` by default) and finally the analysis/handoff label
(`AnalyzedLabel` in `workflow_labels`, default `ai-analyzed`). Long plans are split into
numbered Jira comments without truncation.

The manager continues scanning eligible, unclaimed issues even after they carry
the completion label. It stores Jira's source `updated` timestamp and a SHA-256
fingerprint of the title, description and mapped repository. Changed requirements
or repository mappings automatically trigger replanning; unchanged plans are
reused. Agent comments and labels change Jira's timestamp but not the fingerprint,
so they do not cause unnecessary paid planning runs. Existing plans with no
fingerprint are replanned once after migration.

Before replacing a stale plan, the manager removes both handoff/completion labels.
It refreshes the issue before planning and again before saving and publishing,
discarding generated plans if requirements changed in the meantime. Issues
claimed by the worker are excluded, including with custom JQL. Manager and worker
analysis labels must match; the manager reads the worker's configured pickup
label at startup.

A current saved plan is reused on retries, so a Jira or label failure does not trigger
another agent run. If posting fails partway through a multi-comment plan, retry
may duplicate comments already posted. Database and Jira writes are not a
single transaction. Failures are logged and reported to Jira with
the shared failure label (`ai-agent-failed` by default). Failed tickets are excluded
from polling, including custom JQL, to avoid unlimited paid planning attempts.
Resolve the failure and remove the failure label to retry; any saved plan is
reused without another Copilot run. If Jira cannot accept the failure label,
the error is logged and polling can retry the ticket. Plans already
posted to Jira are not posted again when only the analysis or completion label
needs retrying. The analysis label is added even with a custom completion label.

Manager labels are distinct from aiworker's pickup labels, leaving planned
tickets available for implementation. The analysis label is the handoff signal:
aiworker's default query only picks up issues that carry it, and the worker then
implements using the plan stored in `manager_plans`. Because the plan is saved
before the label is added, the worker always finds a stored plan for gated
issues. The worker also checks the fingerprint against fresh Jira requirements;
stale or unpublished plans are never implemented. Apart from the triage outcomes
below, ticket statuses remain unchanged apart from closing merged pull requests.
To deliberately replan an unclaimed ticket, remove its analysis and completion
labels, delete its `manager_plans` row, and clear any failure label. Do not reset
claimed or already pushed work; follow the worker's recovery instructions instead.

## Triage behaviour

Copilot returns one JSON triage document (`decision`, `questions`, `analysis`,
`plan`, `subtasks[{title, description, dependsOn}]`, `riskAreas`). Malformed or
inconsistent JSON (unknown decision, missing fields, dependency cycles) fails the
ticket like any planning failure. Plain-text output is still accepted as a
legacy plan and handed off. Decisions:

| Decision | Behaviour |
| --- | --- |
| `clarify` | Requirements unclear or contradictory: the questions are posted as a Jira comment and `Jira.JiraClarificationLabel` is added. No handoff. The ticket is re-triaged when the title/description change (or the label is removed). |
| `analysis_only` | Only analysis requested: the analysis is written to Jira and the issue is moved to `Jira.JiraAnalysisOnlyStatus` (`Code Review`, in Jira, not GitHub). No code, branch, pull request or handoff label. |
| `small` | Handed off directly with the normal plan flow (no human approval). |
| `needs_approval` | Authentication, database or architecture changes: the plan is posted and `Jira.JiraApprovalLabel` is added. The worker is not triggered until a human adds `Jira.JiraApprovedLabel`; the manager then hands off and removes both labels. |
| `split` | Large ticket: sub-tasks (`Jira.JiraSubtaskIssueType`) are created with descriptions, "Blocks" links for dependencies, and their own `manager_plans` rows. The parent receives a comment with the execution order. Sub-tasks without open dependencies are moved to `Jira.JiraStatusName` and handed off; the others are released when all their dependencies reach `Jira.JiraDoneStatus`. |

Triage outcomes are stored in `manager_plans` with a `[triage:<decision>]` first
line, so no schema change is needed and they are never handed off. If sub-task
creation fails partway, retrying after removing the failure label can create
duplicate sub-tasks; delete the partial ones first. The worker only picks up
sub-tasks that its query matches. Released sub-tasks are placed in the shared
`Jira.JiraStatusName` status selected by both agents. The extra settings are
optional and default as shown; add them to the shared `settings` rows to change them.

## Closing merged pull requests

At the start of every polling cycle for each mapped project, before new tasks are
handled, AITechLead lists the project's issues in `Jira.JiraInReviewStatus`
(`In Review`) and loads their pull requests from `pull_requests`. It then asks the
GitHub API whether each one is merged:

- no stored pull request: the issue stays in `In Review`;
- any pull request open, or closed without merging: the issue stays in `In Review`;
- all pull requests merged: the issue (or sub-task) is moved to `Jira.JiraDoneStatus` (`Done`).

Failures are logged per issue and do not stop the polling cycle. The worker retries
failed pull-request tracking writes from Jira without rerunning implementation.
Pull requests created before the `pull_requests` table existed are not backfilled.

## Run
1. Copy `aiteam/.env.example` to `aiteam/.env`.
2. In the database `settings` table, set shared `Jira.JiraBaseUrl`, `Jira.JiraEmail`, and `Jira.JiraApiToken`.
3. Set shared `GitHub.AgentGitHubToken` to a fine-grained token with **Copilot Requests** permission.
4. Set the `aimanager` row `GitHub.GitHubRepositoryToken` with **Contents: read** and
   **Pull requests: read** permission for all mapped
   repositories and their submodules. The manager needs no push or pull-request
   write permissions. Use a separate read-only token from aiworker when running both.
5. Configure the database file above, and set `MARIADB_ROOT_PASSWORD`,
   `MARIADB_PASSWORD`, `NUGET_USERNAME` and `NUGET_TOKEN` in `aiteam/.env`.
6. Create the external database volume once:

   ```sh
   docker volume create aiteam_mariadb_data
   ```

7. From `aiteam`, run:

   ```sh
   docker compose --env-file .env up --build -d manager-agent
   ```

Compose starts MariaDB and runs dbmigration before starting the manager.
For initial setup, run the migration service separately, populate settings and
`manager_projects`, then start the manager as described in the configuration guide.
The Docker build context is the repository root. The private `DarkFactorAS`
NuGet feed requires `NUGET_USERNAME` and `NUGET_TOKEN`; Docker receives these
through BuildKit secrets, not build arguments:

```sh
docker build -f aiteam/aimanager/Dockerfile \
  --secret id=nuget_username,env=NUGET_USERNAME \
  --secret id=nuget_token,env=NUGET_TOKEN .
```

For local execution, run dbmigration first, configure the database connection in JSON and Jira/GitHub settings in the database,
then run `dotnet run --project aiteam/aimanager/src/ManagerAgent/ManagerAgent.csproj`.

## Tests

Unit tests and their project live in `src/ManagerAgentTests/`:

```sh
dotnet test aiteam/aimanager/src/ManagerAgentTests/ManagerAgent.Tests.csproj
```

The **test aitechlead** VS Code task runs the same command. Tests are not in the
Docker image. `Manager-CI-DotNet` builds the manager and runs unit tests in Release
on pull requests and pushes to `main`; this validates AITechLead itself, not code
from Jira tickets.

## Configuration

All Jira/GitHub entries below are database setting names; only
`PollIntervalSeconds` remains in JSON.

| Setting | Default | Description |
| --- | --- | --- |
| `Jira.JiraBaseUrl` | required | Shared HTTPS Jira instance |
| `Jira.JiraEmail` | required | Jira account email |
| `Jira.JiraApiToken` | required | Jira API token |
| `GitHub.AgentGitHubToken` | required | Token with Copilot Requests permission |
| `GitHub.GitHubRepositoryToken` | required | Read-only access to all mapped repositories |
| `Jira.JiraStatusName` | `Selected for Development` | Eligible ticket and released sub-task status |
| `Jira.JiraJql` | generated | Optional filter/ordering, constrained to each mapped project |
| `Jira.JiraAnalysisOnlyStatus` | `Code Review` | Optional shared setting: status set for analysis-only tickets |
| `Jira.JiraInReviewStatus` | `In Review` | Optional shared setting: worker handoff status; tickets here are moved to Done when all their pull requests are merged |
| `Jira.JiraDoneStatus` | `Done` | Optional shared setting: sub-task status that releases dependants, and the status set when all pull requests are merged |
| `Jira.JiraSubtaskIssueType` | `Sub-task` | Optional shared setting: issue type used for created sub-tasks |
| `Jira.JiraAutoApproveSmallChanges` | `true` | Optional shared setting: `false` holds small changes for approval too |
| `PollIntervalSeconds` | `30` | Poll interval, minimum 5 seconds |

Jira labels live in the `workflow_labels` table (agent `aimanager`), created and seeded
by dbmigration. They are read once at startup, plus the `aiworker` `PickupLabel`
for the worker exclusion query. A missing row fails startup; there is no fallback.

| Code key | Default | Purpose |
| --- | --- | --- |
| `PickupLabel` | `ai-manager-planned` | Completion label |
| `FailureLabel` | `ai-agent-failed` | Failure label |
| `AnalyzedLabel` | `ai-analyzed` | Label added after the plan is posted to Jira |
| `ClarificationLabel` | `ai-needs-clarification` | Added when the requirements are unclear |
| `ApprovalLabel` | `ai-awaiting-approval` | Plan is held for human approval |
| `ApprovedLabel` | `ai-approved` | A human adds it to release a held plan |
