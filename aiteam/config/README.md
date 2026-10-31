# App configuration

All three apps use the BotWebServer pattern: `IConfigurationHelper` registered
as `ConfigurationHelper<AgentSettings>` for the agents and
`ConfigurationHelper<AppSettings>` for aibuildpage, reading
`Config/appsettings.<environment>.json`. Jira/GitHub credentials, labels, queries
and polling settings now live in each agent's JSON file alongside app/database
settings. Environment Jira/GitHub variables are no longer read or used as overrides.
aimanager's project keys and repository URLs remain in the database; aiworker
uses JSON `Jira.JiraProjectKey` and `GitHub.TargetRepositoryUrl`.

Each app (`aiworker`, `aimanager`, `aibuildpage`) has a template for every
supported environment. Copy the selected `appsettings.<environment>.json.example`
to `appsettings.<environment>.json` in the same folder (remove only `.example`):

- `appsettings.Production.json`: deployed database host `aiteam_database`.
- `appsettings.Development.json`: local database host `localhost` and its
  published port (Compose does not publish MariaDB by default).
- `appsettings.DevelopmentDocker.json`: database host `host.docker.internal`
  and the port of the database running on your host.

For example, to configure aimanager for local development:

```sh
cp aiteam/config/aimanager/appsettings.Development.json.example \
   aiteam/config/aimanager/appsettings.Development.json
```

Run this from the repository root, only if the destination does not already
contain your configuration. The same naming convention applies to all three apps.

Set the actual database name, username and password. Do not commit these runtime
files; Git ignores them. The templates contain no credentials. aiworker loads
app configuration but does not connect to the database or run migrations.
aimanager and aibuildpage require a database and run their startup patchers.

Each agent groups its settings under `Jira` and `GitHub`:

```json
{
  "Jira": {
    "JiraBaseUrl": "https://your-company.atlassian.net",
    "JiraEmail": "your-jira-account@example.com",
    "JiraApiToken": ""
  },
  "GitHub": {
    "AgentGitHubToken": "",
    "GitHubRepositoryToken": ""
  }
}
```

Set `Jira.JiraBaseUrl`, `Jira.JiraEmail`, `Jira.JiraApiToken`,
`GitHub.AgentGitHubToken` (Copilot Requests) and `GitHub.GitHubRepositoryToken`
(repository access). Move all existing `Jira*` fields into `Jira`, and move
`AgentGitHubToken`, `GitHubRepositoryToken` and the worker's `TargetRepositoryUrl`
into `GitHub`, keeping their values. Older `GitHubToken` fields must also be
renamed to `AgentGitHubToken`. Flat fields are no longer read.
The manager's `Jira.JiraAnalyzedLabel` defaults to `ai-analyzed`.
`PollIntervalSeconds` is a JSON integer, with minimum 5. Optional omitted settings
use the defaults in the templates. Required empty credentials fail validation.
The manager and worker have separate JSON files, allowing different tokens and labels.
Do not copy agent credentials into aibuildpage, which does not use Jira/GitHub.

`.env` now contains only deployment/build settings: MariaDB container credentials,
volume/config paths, `DOTNET_ENVIRONMENT` and NuGet authentication. Move existing
Jira/GitHub values into each agent's selected JSON file before removing them from
`.env`. A legacy `GH_TOKEN` or `GITHUB_TOKEN` maps to JSON `GitHub.AgentGitHubToken`; there is
no runtime fallback to environment tokens. The runner supplies only this configured
Copilot token to its child process; repository credentials stay with Git.

The selected environment is `DOTNET_ENVIRONMENT` for the console apps; the web
app also honors ASP.NET Core environment selection. Production is the default.
All three apps print the selected environment and `Config/appsettings.<environment>.json`
filename before loading app configuration, including when the file is missing.
VS Code's AI Worker launch selects Development. Configure the JSON file first.

Projects copy configured JSON files into `Config/` in build and publish output.
Startup uses the executable directory as its working directory so configuration
works regardless of the directory from which `dotnet run` is invoked.
Local publishing therefore includes credentials: treat publish output as sensitive.

Docker builds do not copy runtime configuration into the image. Compose mounts
`config/aiworker` and `config/aimanager` read-only at `/app/Config`, controlled by
`AIWORKER_CONFIG_PATH` and `AIMANAGER_CONFIG_PATH`. When deploying aibuildpage
separately, mount its config directory at `<application directory>/Config`.
Use `DOTNET_ENVIRONMENT=DevelopmentDocker` for that environment.

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
