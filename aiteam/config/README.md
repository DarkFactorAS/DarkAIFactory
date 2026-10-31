# App configuration

All three apps use the BotWebServer pattern: `IConfigurationHelper` registered
as `ConfigurationHelper<AppSettings>`, reading
`Config/appsettings.<environment>.json`. Jira/GitHub settings remain in `.env`;
aimanager's project mappings remain in the database.

For each app (`aiworker`, `aimanager`, `aibuildpage`), copy its
`appsettings.Production.json.example` to the following files in the same folder:

- `appsettings.Production.json`: deployed database host `aiteam_database`.
- `appsettings.Development.json`: local database host `localhost` and its
  published port (Compose does not publish MariaDB by default).
- `appsettings.DevelopmentDocker.json`: database host `host.docker.internal`
  and the port of the database running on your host.

Set the actual database name, username and password. Do not commit these runtime
files; Git ignores them. The templates contain no credentials. aiworker loads
app configuration but does not connect to the database or run migrations.
aimanager and aibuildpage require a database and run their startup patchers.

The selected environment is `DOTNET_ENVIRONMENT` for the console apps; the web
app also honors ASP.NET Core environment selection. Production is the default.
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

All app restores require the private DarkFactorAS feed, using `NUGET_USERNAME`
and `NUGET_TOKEN`. Worker and manager Docker builds accept BuildKit secrets
`nuget_username` and `nuget_token`; Compose and CI supply them.
