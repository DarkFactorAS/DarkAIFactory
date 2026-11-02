# aibuildpage

Minimal ASP.NET Core web project that serves a blank page titled "DarkFactor AI Team".

Build: `dotnet build aiteam/aibuildpage/src/aibuildpage/aibuildpage.csproj`

Run: `dotnet run --project aiteam/aibuildpage/src/aibuildpage/aibuildpage.csproj`

## Docker and deployment

Build from the repository root with `NUGET_USERNAME` and `NUGET_TOKEN` set:

```sh
docker build . --file aiteam/aibuildpage/Dockerfile --tag aibuildpage \
  --secret id=nuget_username,env=NUGET_USERNAME \
  --secret id=nuget_token,env=NUGET_TOKEN
```

The image listens on port 8080 and runs as the non-root `app` user. Runtime
configuration is excluded from the image; mount the configured directory
read-only at `/app/Config` using the same `aiteam/config` directory as the other
clients and dbmigration, and provide access to the configured database.
See [deployment setup and Infrastructure blockers](../../README.md#deployment)
before running the GitHub deployment workflow.

## Database

Configure Production, Development or DevelopmentDocker JSON files using
[App configuration](../config/README.md). Files are copied into build/publish
output and loaded from the executable's `Config` directory. Startup selects
the executable directory as its working directory.

The app does not create tables or run migrations. Run the standalone
[dbmigration](../dbmigration/README.md) executable against the same database
before starting this app, including in separate deployments.

On startup, the app loads all shared Jira/GitHub values and its own
`Jira.JiraPickupLabel`, `Jira.JiraAnalyzedLabel`, and `GitHub.GitHubRepositoryToken`
rows through the same loader as both agents. Missing rows fail startup.
Settings are available server-side via DI, never returned by the blank page.
The app currently makes no Jira/GitHub calls; its token can remain empty.
See the configuration guide for migrating existing credentials into the database.

The connection is read from the standard DarkFactor `AppSettings` configuration (`DatabaseConnection`).
`DarkFactor.Common.Lib` is published to the private `DarkFactor` NuGet feed, which must be configured
for restore to succeed.
