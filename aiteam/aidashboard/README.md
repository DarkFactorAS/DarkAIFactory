# aidashboard

ASP.NET Core web project serving a React + Tailwind (Vite) dashboard shell with a
header titled "DarkAIFactoy - AI Team workflow" (spelling taken verbatim from
Jira DFAI-30; confirm before changing). It also exposes `GET /api/health`,
`GET /api/tasks/active-ai`, and `GET /api/stats/solved-by-project`, which
returns `[{"project": "DFAI", "solvedCount": 3}]`
counting Jira issues in the Done status category per project (502 with a generic error if Jira fails;
credentials are never logged).

`GET /api/tasks/active-ai` returns at most 3 Jira issues (`key`, `summary`,
`status`, `description`) that carry an AI workflow label (the `PickupLabel` and
`AnalyzedLabel` of aimanager and the `PickupLabel` of aiworker from
`workflow_labels`) and are not in the Done status. Ordering is most recently
updated first, ties broken by key (default; confirm with the requester).
Jira failures return 502. Tests: `dotnet test aiteam/aidashboard/aidashboard.Tests.csproj`.

The frontend sources sit directly in `aiteam/aidashboard` (`package.json`,
`vite.config.js`, `index.html`, `main.jsx`, `App.jsx`, `index.css`). The Dockerfile builds them
(`npm install && npm run build`) and copies the output to `/app/wwwroot`, which
ASP.NET Core serves as static files with an SPA fallback to `index.html`
(unknown `/api/*` routes return 404). For local frontend work run
`npm install && npm run dev` in `aiteam/aidashboard` (proxies `/api` to localhost:5000).
Without a built `wwwroot`, a plain HTML fallback page with the title is served.

Build: `dotnet build aiteam/aidashboard/src/aidashboard/aidashboard.csproj`

Run: `dotnet run --project aiteam/aidashboard/src/aidashboard/aidashboard.csproj`

## Docker and deployment

Build from the repository root with `NUGET_USERNAME` and `NUGET_TOKEN` set:

```sh
docker build . --file aiteam/aidashboard/Dockerfile --tag aidashboard \
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

Configure Production, Development or Acceptance JSON files using
[App configuration](../config/README.md). Files are copied into build/publish
output and loaded from the executable's `Config` directory. Startup selects
the executable directory as its working directory.

The app does not create tables or run migrations. Run the standalone
[dbmigration](../dbmigration/README.md) executable against the same database
before starting this app, including in separate deployments.

On startup, the app loads all shared Jira/GitHub values and its own
`GitHub.GitHubRepositoryToken` row through the same loader as both agents.
Missing rows fail startup.
Settings are available server-side via DI, never returned by the blank page.
The app makes read-only Jira calls for `/api/tasks/active-ai` and
`/api/stats/solved-by-project`, plus one write: `POST /api/tasks/{key}/comments` (`text`) appends the text
as a new Jira comment on that issue (the description is never changed; 400 on invalid input, 502 if Jira fails); its GitHub token can remain empty.
See the configuration guide for migrating existing credentials into the database.

Task API (table `agent_tasks`, created only by dbmigration): `GET /api/agent-tasks[?status=]`,
`GET /api/agent-tasks/{id}`, `POST /api/agent-tasks` (`projectKey`, `title`, `description`;
created as `New`) and `PUT /api/agent-tasks/{id}` (also `status` New/Created/Failed and
optional `jiraKey`). Invalid input returns 400, unknown ids 404.

The connection is read from the standard DarkFactor `AppSettings` configuration (`DatabaseConnection`).
`DarkFactor.Common.Lib` is published to the private `DarkFactor` NuGet feed, which must be configured
for restore to succeed.
