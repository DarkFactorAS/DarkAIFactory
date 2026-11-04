# App configuration

All three apps use the BotWebServer pattern: `IConfigurationHelper` registered
as `ConfigurationHelper<AgentSettings>` for the agents and
`ConfigurationHelper<AppSettings>` for aibuildpage and dbmigration, reading
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
Both agents use the same `PollIntervalSeconds`; aibuildpage and dbmigration
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

- `shared`: every Jira/GitHub setting except the three below. All clients read
  these same rows, including `Jira.JiraFailureLabel` (now `ai-agent-failed` for
  both agents) and `GitHub.AgentGitHubToken`.
- `aimanager`, `aiworker`, `aibuildpage`: exactly `Jira.JiraPickupLabel`,
  `Jira.JiraAnalyzedLabel`, and `GitHub.GitHubRepositoryToken`.
  Each client reads its own three rows; no fallback to another client or shared
  row is allowed. Missing rows fail explicitly.

The standalone `dbmigration` executable owns all table creation. It retains
`StartupDatabasePatcher` IDs 1-3 for logging, manager projects and plans, adds
ID 4 for plan freshness metadata, and retains
`IntegrationSettings` IDs 1-2 for settings and seed data. Existing migration
history remains valid; dbmigration upgrades the `version` primary key to
(`system`, `patchId`) so IDs from different systems do not collide.
Defaults are inserted once; subsequent migrations do not
overwrite configured values. Credentials and the worker repository URL are
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
UPDATE settings SET value = 'ai-manager-planned'
WHERE client = 'aimanager' AND name = 'Jira.JiraPickupLabel';
```

Shared settings are `Jira.JiraBaseUrl`, `Jira.JiraEmail`, `Jira.JiraApiToken`,
`Jira.JiraProjectKey` (`DFAI`), `Jira.JiraStatusName` (`Selected for Development`),
`Jira.JiraJql` (empty for generated JQL), `Jira.JiraFailureLabel`
(`ai-agent-failed`), `Jira.JiraInProgressStatus` (`In Progress`),
`Jira.JiraInReviewStatus` (`In Review`), `GitHub.AgentGitHubToken` (Copilot
Requests) and `GitHub.TargetRepositoryUrl`. The `aiworker` client also has an
optional `GitHub.AgentModel` (seeded as `gpt-5-mini`) passed to the Copilot CLI
as `--model`; blank uses the CLI default.

Optional AITechLead triage settings (shared, default used when the row is absent):
`Jira.JiraClarificationLabel` (`ai-needs-clarification`), `Jira.JiraApprovalLabel`
(`ai-awaiting-approval`), `Jira.JiraApprovedLabel` (`ai-approved`),
`Jira.JiraAnalysisOnlyStatus` (`Code Review`), `Jira.JiraDoneStatus` (`Done`),
`Jira.JiraSubtaskIssueType` (`Sub-task`) and `Jira.JiraAutoApproveSmallChanges`
(`true`). See the [aimanager README](../aimanager/README.md#triage-behaviour).

Both agents use `manager_projects` for project/repository selection, reloading
mappings each polling cycle. Shared `Jira.JiraProjectKey` and
`GitHub.TargetRepositoryUrl` are only worker fallbacks when that table is empty.
No fallback occurs for invalid mappings or database read failures. The worker's
repository token must have Contents and Pull requests read/write permission
for every mapped repository.

Client pickup defaults are `ai-manager-planned`, `ai-agent-picked` and
`ai-build-picked`, respectively. All analyzed defaults are `ai-analyzed`.
The manager needs a read-only repository token (Contents and Pull requests read, to
check whether pull requests are merged); the worker needs Contents and
Pull requests read/write. Manager and worker analysis labels must match for
handoff. The manager also reads the worker pickup label to avoid replanning
claimed issues. After a failed planning attempt, clear the shared failure label
only after resolving the error to resume automatic planning.
aibuildpage loads the same settings server-side but
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
VS Code's AI Worker launch selects Development. Configure the JSON file first.

Projects copy configured JSON files into `Config/` in build and publish output.
Startup uses the executable directory as its working directory so configuration
works regardless of the directory from which `dotnet run` is invoked.
Local publishing therefore includes credentials: treat publish output as sensitive.

Docker builds do not copy runtime configuration into the image. Compose mounts
the same `config` directory read-only at `/app/Config` for all services,
controlled by `AITEAM_CONFIG_PATH` (default `./config`).
Both agents depend on successful completion of the
one-shot dbmigration service, which waits for MariaDB's health check.
When deploying aibuildpage
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
