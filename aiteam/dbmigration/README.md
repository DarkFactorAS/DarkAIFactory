# dbmigration

Standalone .NET 10 executable that creates/upgrades the database used by
aiagent and aidashboard. Clients only read/write data; none creates
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
| `StartupDatabasePatcher` | 5 | `pull_requests` (Jira key, repository, PR number, unique PR URL), written by the retired aiworker; dropped by `LegacyAgentCleanup` 2 |
| `IntegrationSettings` | 1 | `settings` with shared/client scope constraint |
| `IntegrationSettings` | 2 | Default settings rows (empty credentials) |
| `IntegrationSettings` | 10 | Scope constraint allows the `aiclaudeworker` client (`GitHub.GitHubRepositoryToken`, `Claude.AnthropicApiKey`, `Claude.AgentModel`) |
| `IntegrationSettings` | 11 | `aiclaudeworker` rows (empty credentials, model `claude-sonnet-5-5`) |
| `WorkflowLabels` | 4 | `aiclaudeworker` labels, equal to the `aiworker` defaults |
| `AgentTasks` | 1 | `agent_tasks` (`CREATE TABLE IF NOT EXISTS`; project key, title, description, status New/Created/Failed, Jira key, created/updated), used by the dashboard task API |
| `ReviewCommentAttempts` | 1 | `review_comment_attempts` (`CREATE TABLE IF NOT EXISTS`; used by the retired review worker; dropped by `LegacyAgentCleanup` 1) |
| `LegacyAgentCleanup` | 1-3 | `DROP TABLE IF EXISTS` for `review_comment_attempts`, `pull_requests` and `manager_plans` (tables used only by the retired agents) |
| `LegacyAgentCleanup` | 4 | Deletes `workflow_labels` rows of `aimanager`, `aiworker` and `aiclaudeworker` |
| `LegacyAgentCleanup` | 5-6 | Deletes `settings` rows of the clients `aimanager`, `aiworker`, `aiclaudeworker`, `aibuildpage` and the unused `aidashboard` rows `Jira.JiraPickupLabel`, `Jira.JiraAnalyzedLabel`, `GitHub.GitHubRepositoryToken`, `GitHub.AgentModel` |
| `LegacyAgentCleanup` | 7 | Replaces `ck_settings_scope`: `shared` (non client-scoped names), `aidashboard` (`Dashboard.AiLabels`, `Dashboard.ActiveStatuses`), `aiagent` (`GitHub.GitHubRepositoryToken`, `Claude.AnthropicApiKey`) |
| `LegacyAgentCleanup` | 8 | Removes the legacy labels `ai-manager-planned`, `ai-analyzed`, `ai-agent-picked` from `Dashboard.AiLabels`; if nothing is left, uses the `aiagent` labels from `workflow_labels` |

Existing databases reuse their migration history. Already applied patches are
skipped; configured values, mappings and plans are preserved. Back up the database
before upgrades. Patch 4 uses `ADD COLUMN IF NOT EXISTS` so it can recover from a
failed history write without losing plans. Existing rows have null freshness
metadata.

### Removing the legacy agents (DFAI-215)

`LegacyAgentCleanup` permanently deletes the history of the retired agents
(`manager_plans`, `pull_requests`, `review_comment_attempts`, and their labels and settings
rows). `manager_projects`, `agent_tasks`, all `agent_*` tables and `logtable` are kept.
Existing patches are never edited because fresh databases replay them in order; the cleanup
is a forward-only system whose patches are all idempotent.

> **Production deployment is blocked** until `.github/workflows/aiteam-deploy.yml`
> no longer deploys `aimanager`, `aiworker`, `aiclaudeworker`, `aireviewworker` and
> `aijiramanager`. Their project directories are removed, so the current workflow
> fails before it reaches `aiagent`. Update the workflow to deploy only the remaining
> services (and make `aiagent` stop depending on `aireviewworker`) as part of this
> rollout, before step 3.

Rollout:

1. Stop any old agent that is still running.
2. Back up the database.
3. Deploy the aidashboard and aiagent versions that no longer read the legacy labels
   and clients, using a deployment workflow from which the retired agents have been
   removed (see the note above).
4. Run dbmigration.
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
