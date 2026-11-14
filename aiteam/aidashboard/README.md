# aidashboard

ASP.NET Core web project serving a React + Tailwind (Vite) dashboard shell with a
header titled "DarkAIFactory - AI Team workflow" (the earlier
"DarkAIFactoy" typo from DFAI-30 was fixed in DFAI-49), centred. It also exposes `GET /api/health`,
`GET /api/tasks/active-ai`, and `GET /api/stats/solved-by-project`, which
returns `[{"project": "DFAI", "solvedCount": 3, "aiCount": 1, "nonAiCount": 2}]`
counting Jira issues in the Done status category per project, split into `aiCount` (issues carrying a configured AI workflow pickup or analyzed label) and `nonAiCount` (502 with a generic error if Jira fails;
credentials are never logged).

`GET /api/tasks/active-ai` returns at most 3 Jira issues (`key`, `summary`,
`status`, `description`) that carry an AI workflow label (the `PickupLabel` and
`AnalyzedLabel` of aimanager and the `PickupLabel` of aiworker from
`workflow_labels`) and are not in the Done status. Ordering is most recently
updated first, ties broken by key (default; confirm with the requester).
The dashboard's "Top 3 AI tasks" panel polls this endpoint every 30 seconds
(background refreshes keep the current list; a failed poll keeps the last list
and shows the error).
Jira failures return 502. Tests: `dotnet test aiteam/aidashboard/tests/aidashboard.Tests.csproj`.

The frontend sources live in `aiteam/aidashboard` (`package.json`,
`vite.config.js`, `index.html`) with the JavaScript/JSX sources in `js/`
(`main.jsx`, `App.jsx`, `TaskEditor.jsx`, `AddTextPanel.jsx`, `useApiData.js`) and the
stylesheets in `css/` (`index.css`). The Dockerfile builds them
(`npm install && npm run build`) and copies the output to `/app/wwwroot`, which
ASP.NET Core serves as static files with an SPA fallback to `index.html`
(unknown `/api/*` routes return 404). For local frontend work run
`npm install && npm run dev` in `aiteam/aidashboard` (proxies `/api` to localhost:5000).
Without a built `wwwroot`, a plain HTML fallback page with the title is served.

Build: `dotnet build aiteam/aidashboard/aidashboard.csproj`

Run: `dotnet run --project aiteam/aidashboard/aidashboard.csproj`

## Docker and deployment

Build from the repository root with `NUGET_USERNAME` and `NUGET_TOKEN` set:

```sh
docker build . --file aiteam/aidashboard/Dockerfile --tag aidashboard \
  --secret id=nuget_username,env=NUGET_USERNAME \
  --secret id=nuget_token,env=NUGET_TOKEN
```

The image listens on port 8080 and runs as the non-root `app` user. Runtime
configuration is excluded from the image; Compose mounts the separate
`aiteam/aidashboard/runtime-config` directory read-only at `/app/Config`. To configure
it for Compose, create the runtime file from the template and set its database
values to match `aiteam/.env`:

```sh
mkdir -p aiteam/aidashboard/runtime-config
cp aiteam/aidashboard/Config/appsettings.template.json \
  aiteam/aidashboard/runtime-config/appsettings.Production.json
```

Set `DatabaseConnection.Server` to `aiteam_database` and set the database,
username and password to `MARIADB_DATABASE`, `MARIADB_USER` and
`MARIADB_PASSWORD`. Set `AIDASHBOARD_CONFIG_PATH` in `aiteam/.env` to override
the default runtime-config directory. Runtime JSON files in that directory are
ignored by Git.
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
as a new Jira comment on that issue (the description is never changed; 400 on invalid input,
502 if Jira fails or times out); its GitHub token can remain empty.
Comment submissions require a bearer write token configured server-side through
`Dashboard__WriteToken`. Set a strong secret using the deployment's secret management,
serve the dashboard over HTTPS, and enter that token in the comment form. It is not
stored in browser storage or returned by an API. Missing configuration disables comment
writes (401). Only issues currently returned by the active AI task list in the configured
Jira project are authorized (403 otherwise).
`GET /api/tasks/config` returns only the non-secret configured `projectKey` used by task creation.
AI labels: `GET /api/settings/ai-labels` returns `{ labels }`; `PUT /api/settings/ai-labels` (`labels`, Bearer write
token required, 401 otherwise) validates (max 20 labels, 1-255 chars, no whitespace or commas; 400 otherwise), writes
only `settings[aidashboard, Dashboard.AiLabels]` and updates the running app immediately, no restart needed. An empty
list means: use the workflow pickup/analyzed labels. The "Edit AI labels" button in the "Tasks solved per project"
panel uses it and redraws the graph; the labels also drive the "Top 3 AI tasks" panel.
See the configuration guide for migrating existing credentials into the database.

Task API (table `agent_tasks`, created only by dbmigration): `GET /api/agent-tasks[?status=]`,
`GET /api/agent-tasks/{id}`, `POST /api/agent-tasks` (`projectKey`, `title`, `description`;
created as `New`) and `PUT /api/agent-tasks/{id}` (also `status` New/Created/Failed and
optional `jiraKey`). Invalid input returns 400, unknown ids 404.
The dashboard editor instead uses `PATCH /api/agent-tasks/{id}` (`title`, `description`),
which atomically edits only tasks still in `New` status, preserves workflow fields,
and returns 409 if the task has progressed.

Agent slots UI (`js/AgentSlots.jsx`, `js/AgentEditor.jsx`, `js/agentApi.js`, `js/WriteToken.jsx`) sits above the
task editor in the centre column and uses `/api/agents` (writes need the Bearer write token) plus the read-only
`GET /api/agents/projects` (sorted `jira_project_key` values). It polls every 30 s (the aiAgent default poll
interval). A card shows Disabled when not enabled, otherwise Offline when there is no status or the heartbeat is
older than 90 s (3 polls), else Idle / `Running on KEY` / Error. While Running (and not Offline) the capability chip matching `status.currentCapability` is highlighted (added if not enabled). `heartbeatAt` is treated as UTC (the heartbeat
writer must store UTC); "now" comes from the response `Date` header. The write token is asked for in the page and
kept in memory only (never in browser storage); a 401 clears it. No write request is sent without a token.
There is no frontend test runner; API tests are in `tests/AgentEndpointsTests.cs`.

Manual check: cards appear above the task editor; create via `+`; edit; toggle enable/disable; delete shows the
inline Confirm/Cancel; status changes within one poll after updating `agent_status`; Offline after a stale
heartbeat; the Network tab shows no write without a token, and a bad token gives 401 and asks again.

The connection is read from the standard DarkFactor `AppSettings` configuration (`DatabaseConnection`).
`DarkFactor.Common.Lib` is published to the private `DarkFactor` NuGet feed, which must be configured
for restore to succeed.

The centre panel list of tasks not yet sent to Jira (`status=New`) refreshes every 30 seconds, so tasks
created in Jira disappear without a page reload; an open edit form keeps its input across refreshes.
