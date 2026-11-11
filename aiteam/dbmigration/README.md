# dbmigration

Standalone .NET 10 executable that creates/upgrades the database used by
aimanager, aiworker, aiclaudeworker and aidashboard. Clients only read/write data; none creates
tables or applies migrations.

## Configuration and local execution

Copy the appropriate template from the shared `../config/` directory to the same filename
without `.example`, preserving any existing runtime config. Set
`DatabaseConnection` once for all clients and dbmigration.
See [App configuration](../config/README.md) for environments and credentials.

From the repository root:

```sh
dotnet build aiteam/dbmigration/src/dbmigration/dbmigration.csproj --configuration Debug
DOTNET_ENVIRONMENT=Development dotnet run --project aiteam/dbmigration/src/dbmigration/dbmigration.csproj
dotnet test aiteam/dbmigration/tests/dbmigration.Tests/dbmigration.Tests.csproj
```

VS Code provides **build dbmigration**, **run dbmigration** and **test dbmigration**
tasks. The run task uses `DOTNET_ENVIRONMENT` from the process environment
(Production by default).

The program prints the environment and database name/host/port without
credentials, waits for the connection, applies migrations and exits. Success
returns exit code 0; a reported patch failure returns 1 and exceptions also fail
the process. Do not start clients unless migration completed successfully.

## Tables and upgrade compatibility

Existing migration systems and IDs are retained; new upgrades append patches:

| System | ID | Operation |
| --- | --- | --- |
| `StartupDatabasePatcher` | 1 | Base logging table (`logtable`) |
| `StartupDatabasePatcher` | 2 | `manager_projects` |
| `StartupDatabasePatcher` | 3 | `manager_plans` |
| `StartupDatabasePatcher` | 4 | Nullable plan fingerprint (`requirements_hash`) and Jira source timestamp (`source_updated`) |
| `StartupDatabasePatcher` | 5 | `pull_requests` (Jira key, repository, PR number, unique PR URL), written by aiworker and read by AITechLead |
| `IntegrationSettings` | 1 | `settings` with shared/client scope constraint |
| `IntegrationSettings` | 2 | Default settings rows (empty credentials) |
| `IntegrationSettings` | 10 | Scope constraint allows the `aiclaudeworker` client (`GitHub.GitHubRepositoryToken`, `Claude.AnthropicApiKey`, `Claude.AgentModel`) |
| `IntegrationSettings` | 11 | `aiclaudeworker` rows (empty credentials, model `claude-sonnet-5-5`) |
| `WorkflowLabels` | 4 | `aiclaudeworker` labels, equal to the `aiworker` defaults |
| `AgentTasks` | 1 | `agent_tasks` (`CREATE TABLE IF NOT EXISTS`; project key, title, description, status New/Created/Failed, Jira key, created/updated), used by the dashboard task API |
| `AgentConfig` | 1 | `agents` (unique `name`, `role`, `behavior_prompt` generic task/behavior prompt, `language_model`, `enabled`, created/updated) |
| `AgentConfig` | 2 | `agent_capabilities` (PK `agent_id`, `capability`, `language_model`; `enabled` flag per capability and per language model; cascades with agent) |
| `AgentConfig` | 3 | `agent_task_metrics`: one row per task run (`agent_id`, optional `agent_task_id`/`jira_key`, `language_model`, `input_tokens`, `output_tokens`, `tool_calls`, `duration_ms`, `succeeded`, `started`, `finished`) |
| `AgentConfig` | 4 | `agent_metrics_totals`: per-agent running totals (tokens, tool calls, `total_duration_ms`, `tasks_succeeded`, `tasks_failed`) |
| `AgentConfig` | 5-7 | Seed `sample-agent` (role Software developer, model `claude-sonnet-5-5`), its capabilities (`read_files`, `edit_files`, `run_shell` on; `web_search` off) and an empty totals row. Seeding is guarded so reruns do not duplicate or overwrite rows |
| `ReviewCommentAttempts` | 1 | `review_comment_attempts` (`CREATE TABLE IF NOT EXISTS`; `pull_request_id` FK to `pull_requests` with cascade delete, `github_comment_id`, unique per pull request, `attempts`, `status` Pending/InProgress/Resolved/Failed, created/updated, nullable `last_error`) |
| `AgentDefinitions` | 1-7 | `agent_definitions`, `agent_definition_capabilities`, `language_models`, and `agent_metrics`; seeds model `gpt-5-mini`, a default agent, and its metrics row with idempotent statements |

`AgentConfig` and `AgentDefinitions` use separate migration systems because they
define different agent schemas. `AgentDefinitions` uses a distinct capabilities
table so it does not conflict with `AgentConfig`'s `agent_capabilities`.
The patches only create new tables (`CREATE TABLE IF NOT EXISTS`,
guarded seeds), so they apply on an empty database and on an existing one without
altering existing tables or data. `agent_task_id` in the `agent_task_metrics` table
intentionally has no foreign key to `agent_tasks`, leaving that table unchanged.

Existing databases reuse their migration history. Already applied patches are
skipped; configured values, mappings and plans are preserved. Back up the database
before upgrades. Patch 4 uses `ADD COLUMN IF NOT EXISTS` so it can recover from a
failed history write without losing plans. Existing rows have null freshness
metadata; the manager replans eligible unclaimed issues once, and the worker
waits instead of implementing unversioned plans. Stop both agents, run migration,
then deploy/restart both updated agents together.
Before applying patches, dbmigration upgrades the library's
`version` table primary key from `patchId` alone to (`system`, `patchId`), retaining
existing rows and allowing both migration systems to use their original IDs.
Settings creation and seeding can recover from a previous failed history write
without overwriting existing setting values.
Run one migration process at a time; do not run it concurrently
with older clients that still perform startup migrations.

## Docker and Compose

Configure `aiteam/config/appsettings.Production.json` (or the selected
environment) before running:

```sh
docker compose --env-file aiteam/.env -f aiteam/docker-compose.yml run --build --rm dbmigration
```

Populate the empty credential settings after the initial migration, then start
the agents. Normal Compose startup also runs the one-shot `dbmigration` service
after MariaDB is healthy; both agents wait for its successful completion.
`AITEAM_CONFIG_PATH` overrides the shared read-only `/app/Config` mount for all services.
For upgrades, explicitly rerun the command above before restarting clients.

The Docker build uses the same private NuGet feed and BuildKit secrets as the
clients:

```sh
docker build . -f aiteam/dbmigration/Dockerfile -t dbmigration \
  --secret id=nuget_username,env=NUGET_USERNAME \
  --secret id=nuget_token,env=NUGET_TOKEN
```

For deployments outside Compose, run this image or executable once with database
access and the config mounted before deploying any client. Use a migration
database account with DDL permissions; clients no longer need table-creation
permissions.
