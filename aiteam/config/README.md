# App configuration

aidashboard no longer uses this folder; see
`aiteam/aidashboard/Config/README.md`.

The agents and dbmigration use the BotWebServer pattern: `IConfigurationHelper` registered
as `ConfigurationHelper<AgentSettings>` for the agents and
`ConfigurationHelper<AppSettings>` for aidashboard and dbmigration, reading
`Config/appsettings.<environment>.json`. JSON contains app/database settings and
the agents' `PollIntervalSeconds` (integer, minimum 5), not Jira/GitHub settings.
All three clients load Jira/GitHub settings from the same MariaDB `settings`
table at startup, using the same loader. All four programs use the same
environment-specific JSON files directly in this `aiteam/config` directory.
Restart clients after changing settings.
There are no JSON or environment Jira/GitHub overrides or fallbacks.

One shared template covers all four programs for each supported environment.
Copy the selected `appsettings.<environment>.json.example`
to `appsettings.<environment>.json` in the same folder (remove only `.example`):

- `appsettings.Production.json`: deployed database host `aiteam_database`.
- `appsettings.Development.json`: local database host `localhost` and its
  published port (Compose does not publish MariaDB by default).
- `appsettings.Acceptance.json`: database host `host.docker.internal`
  and the port of the database running on your host.

For example, to configure all programs for local development:

```sh
cp aiteam/config/appsettings.Development.json.example \
   aiteam/config/appsettings.Development.json
```

Run this from the repository root, only if the destination does not already
contain your configuration. `AppName` is shared (`DarkAIFactory` in the templates).
Both agents use the same `PollIntervalSeconds`; aidashboard and dbmigration
ignore this agent-only property.

When upgrading from per-client directories, move one selected configuration for
each environment directly into `aiteam/config` and reconcile any differing
database values before removing old copies. The old directories are no longer
read. Replace `AIWORKER_CONFIG_PATH`, `AIMANAGER_CONFIG_PATH` and
`DBMIGRATION_CONFIG_PATH` in `.env` with one `AITEAM_CONFIG_PATH` if overriding
the default `./config` mount.

Set the actual database name, username and password. Do not commit these runtime
files; Git ignores them. The templates contain no credentials. All three clients
require an already migrated database and never create tables at startup.
Run [dbmigration](../dbmigration/README.md) before starting any client.

## Shared Jira/GitHub settings

The `settings` table has a composite primary key (`client`, `name`) and a `value`
column. Names use the former JSON section/property notation, for example
`Jira.JiraApiToken`. A database check constraint enforces the scopes:

- `shared`: every Jira/GitHub setting except the exceptions below. All clients read
  these same rows, including `GitHub.AgentGitHubToken`.
- `aiagent`: exactly `GitHub.GitHubRepositoryToken` and `Claude.AnthropicApiKey`
  (optional; empty uses the Claude Code CLI login). `Claude.*` names are never
  allowed on `shared`.
- `aidashboard`: exactly `Dashboard.AiLabels` and `Dashboard.ActiveStatuses`.
  Each client reads its own rows; no fallback to another client or shared
  row is allowed. Missing rows fail explicitly.

Jira labels are not settings. They live in the `workflow_labels` table
(`agent_name`, `code_key`, `label_name`, `description`), created by the
`WorkflowLabels` dbmigration patches (code key = the name of the former code
constant). Only `aiagent` has rows (the rows of the removed legacy agents were deleted by the
`LegacyAgentCleanup` migration); agents read them once at startup and
fail if a row is missing. Edit `label_name` in the table to rename a label.

| Agent | Code key | Default label |
| --- | --- | --- |
| `aiagent` | `PickupLabel` | `aiagent-picked` |
| `aiagent` | `AnalyzedLabel` | `aiagent-analyzed` |
| `aiagent` | `PlannedLabel` | `aiagent-planned` |

The standalone `dbmigration` executable owns all table creation. It retains
`StartupDatabasePatcher` IDs 1-3 for logging, manager projects and plans, adds
ID 4 for plan freshness metadata, and retains
`IntegrationSettings` IDs 1-2 for settings and seed data. Existing migration
history remains valid; dbmigration upgrades the `version` primary key to
(`system`, `patchId`) so IDs from different systems do not collide.
Defaults are inserted once; subsequent migrations do not
overwrite configured values. Credentials and the target repository URL are
initially empty and must be populated by an administrator.

### Migrating existing configuration

1. Stop the agents and back up the database.
2. Configure the shared JSON `DatabaseConnection` for all programs.
3. Run dbmigration to create/seed the tables. In Compose, run
   `docker compose --env-file aiteam/.env -f aiteam/docker-compose.yml run --build --rm dbmigration`
   from the repository root.
4. Using Adminer or another trusted database tool, update the seeded rows.
   Move the existing common Jira/GitHub values to `client = 'shared'`, choosing
   one value where the old clients differed. Move only the three exceptions
   to their respective client rows. Do not copy real tokens into SQL files,
   source control, shell history or logs.
5. Remove the old `Jira` and `GitHub` sections from runtime JSON and restart
   the agents. Existing JSON values are ignored, never imported automatically.

Example non-secret updates:

```sql
UPDATE settings SET value = 'https://your-company.atlassian.net'
WHERE client = 'shared' AND name = 'Jira.JiraBaseUrl';
UPDATE settings SET value = 'https://github.com/owner/repository.git'
WHERE client = 'shared' AND name = 'GitHub.TargetRepositoryUrl';
```

Shared settings are `Jira.JiraBaseUrl`, `Jira.JiraEmail`, `Jira.JiraApiToken`,
`Jira.JiraStatusName` (`Selected for Development`),
`Jira.JiraJql` (empty for generated JQL), `Jira.JiraInProgressStatus` (`In Progress`),
`Jira.JiraInReviewStatus` (`In Review`), `GitHub.AgentGitHubToken` (the GitHub Copilot
credential, not a repository token) and `GitHub.TargetRepositoryUrl`. `GitHub.TargetRepositoryUrl`. The Claude model is `Claude.AgentModel` (seeded as `claude-sonnet-5-5`);
the Claude Code CLI runs `claude -p` with its own login (`claude auth login`, stored in a Docker
volume); `Claude.AnthropicApiKey` is an optional override.
The agent (`aiagent`, see [its README](../aiagent/README.md)) reads the same
`appsettings.<Environment>.json` (`PollIntervalSeconds`, `AgentsPerInstance` 1-3 agents per container, default 1,
overridden by the env var `AIAGENT_AGENTS_PER_INSTANCE`); its runner uses a separate
Claude Code login in the `aiteam_aiagent_claude_config` volume (`./login_claude.sh`, on Windows `login_claude.cmd`).

Optional planning settings (shared, default used when the row is absent):
`Jira.JiraAnalysisOnlyStatus` (`Code Review`), `Jira.JiraDoneStatus` (`Done`),
`Jira.JiraSubtaskIssueType` (`Sub-task`), `Jira.JiraTaskIssueType` (`Task`) and
`Jira.JiraAutoApproveSmallChanges` (`true`).

aiagent uses `manager_projects` for project/repository selection, reloading
mappings each polling cycle. The dashboard also uses the `manager_projects` keys for its
active tasks and comment checks. The former `Jira.JiraProjectKey` setting is no longer read
(existing rows are ignored); `GitHub.TargetRepositoryUrl` is only a fallback when that table is empty.
No fallback occurs for invalid mappings or database read failures. The agent's
repository token must have Contents and Pull requests read/write permission
for every mapped repository.

The agent's repository token needs Contents and Pull requests read/write permission.
aidashboard loads the same settings server-side but
currently performs no Jira/GitHub operations, so its repository token may remain
empty. Its page never exposes settings or credentials.

Treat the database, backups and administration access as secret-bearing:
tokens are stored as text. Use restricted database access and TLS for remote
connections. No credentials are seeded, logged or returned by the web app.

`.env` now contains only deployment/build settings: MariaDB container credentials,
volume/config paths, `DOTNET_ENVIRONMENT` and NuGet authentication. Move existing
Jira/GitHub values into the database before removing them from
`.env`. A legacy `GH_TOKEN` or `GITHUB_TOKEN` maps to shared `GitHub.AgentGitHubToken`; there is
no runtime fallback to environment tokens. The runner supplies only this configured
Copilot token to its child process; repository credentials stay with Git.

The selected environment is `DOTNET_ENVIRONMENT` for the console apps; the web
app also honors ASP.NET Core environment selection. Production is the default.
All four apps print the selected environment and `Config/appsettings.<environment>.json`
filename before loading app configuration, including when the file is missing.
After loading configuration, all four apps print the database name, host and
port before resolving database services and connecting.
Database credentials are not included in this message.
 Configure the JSON file first.

Projects copy configured JSON files into `Config/` in build and publish output.
Startup uses the executable directory as its working directory so configuration
works regardless of the directory from which `dotnet run` is invoked.
Local publishing therefore includes credentials: treat publish output as sensitive.

Docker builds do not copy runtime configuration into the image. Compose mounts
the same `config` directory read-only at `/app/Config` for all services,
controlled by `AITEAM_CONFIG_PATH` (default `./config`).
Both agents depend on successful completion of the
one-shot dbmigration service, which waits for MariaDB's health check.
When deploying aidashboard
separately, mount this same shared directory at `<application directory>/Config`.
Use `DOTNET_ENVIRONMENT=Acceptance` for that environment.

## Database administration

Compose includes Adminer, following GameServer's setup. Start it with the
database from the repository root:

```sh
docker compose --env-file aiteam/.env -f aiteam/docker-compose.yml up -d adminer
```

Open `http://localhost:8180` (or the `ADMINER_PORT` configured in `.env`).
Adminer is bound only to localhost and waits for the database health check.
Select MySQL and use server `aiteam_database`, with the database name, username
and password configured by `MARIADB_DATABASE`, `MARIADB_USER` and
`MARIADB_PASSWORD`. The server is preselected; credentials are not passed to
Adminer. Create the external MariaDB volume before first startup as described
in the Compose file. For remote deployments, use an SSH tunnel rather than
exposing Adminer publicly.

All app restores require the private DarkFactorAS feed, using `NUGET_USERNAME`
and `NUGET_TOKEN`. Worker and manager Docker builds accept BuildKit secrets
`nuget_username` and `nuget_token`; Compose and CI supply them.
