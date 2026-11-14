# aidashboard

ASP.NET Core web project serving a React + Tailwind (Vite) dashboard shell with a
header titled "DarkAIFactory - AI Team workflow" (the earlier
"DarkAIFactoy" typo from DFAI-30 was fixed in DFAI-49), centred. It also exposes `GET /api/health`,
`GET /api/tasks/active-ai`, and `GET /api/stats/solved-by-project`, which
returns `[{"project": "DFAI", "solvedCount": 3, "aiCount": 1, "nonAiCount": 2}]`
counting Jira issues in the Done status category per project (limited to the projects builders pick up: `agent_projects` of enabled, non-deleted agents with the Builder capability, joined to `manager_projects`; all `manager_projects` when no agents are defined), split into `aiCount` (issues carrying a configured AI workflow pickup or analyzed label) and `nonAiCount` (502 with a generic error if Jira fails;
credentials are never logged).

`GET /api/tasks/active-ai` returns at most 5 Jira issues (`key`, `summary`,
`status`, `description`, `labels`) that carry an AI workflow label (the `PickupLabel` and
`AnalyzedLabel` of aimanager and the `PickupLabel` of aiworker from
`workflow_labels`) and are not in the Done status. Ordering is most recently
updated first, ties broken by key (default; confirm with the requester).
The dashboard's "Top 5 AI tasks" panel polls this endpoint every 30 seconds
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
serve the dashboard over HTTPS, and enter that token in the comment form. It is remembered in the
`dashboard_write_token` browser cookie (see below) and is never returned by an API. Missing configuration disables comment
writes (401). Only issues currently returned by the active AI task list in the configured
Jira project are authorized (403 otherwise).
`GET /api/tasks/config` returns only the non-secret configured `projectKey` used by task creation.
AI labels: `GET /api/settings/ai-labels` returns `{ labels }`; `PUT /api/settings/ai-labels` (`labels`, Bearer write
token required, 401 otherwise) validates (max 20 labels, 1-255 chars, no whitespace or commas; 400 otherwise), writes
only `settings[aidashboard, Dashboard.AiLabels]` and updates the running app immediately, no restart needed. An empty
list means: use the workflow pickup/analyzed labels. The "Edit AI labels" button in the "Tasks solved per project"
panel uses it and redraws the graph; the labels also drive the "Top 5 AI tasks" panel.

Active statuses: `GET /api/settings/active-statuses` returns `{ statuses }`; `PUT /api/settings/active-statuses`
(`statuses`, Bearer write token required, 401 otherwise) validates (max 20 statuses, 1-255 chars, no commas; spaces
are allowed; 400 otherwise), writes only `settings[aidashboard, Dashboard.ActiveStatuses]` and updates the running app
immediately. An empty list means: show every non-Done status. The cogwheel button in the "Top 5 AI tasks" header
opens the editor and reloads the panel after saving.

Manual check (cogwheel): open the cogwheel, save `In Progress` with a token and the list narrows without a reload;
an empty value restores all non-Done statuses; a bad token gives 401 and the token prompt; the list survives a reload.
See the configuration guide for migrating existing credentials into the database.

Task API (table `agent_tasks`, created only by dbmigration): `GET /api/agent-tasks[?status=]`,
`GET /api/agent-tasks/{id}`, `POST /api/agent-tasks` (`projectKey`, `title`, `description`;
created as `New`) and `PUT /api/agent-tasks/{id}` (also `status` New/Created/Failed and
optional `jiraKey`). Invalid input returns 400, unknown ids 404.
The dashboard editor instead uses `PATCH /api/agent-tasks/{id}` (`title`, `description`),
which atomically edits only tasks still in `New` status, preserves workflow fields,
and returns 409 if the task has progressed.

Agent slots UI (`js/AgentSlots.jsx`, `js/AgentEditor.jsx`, `js/agentApi.js`, `js/WriteToken.jsx`) sits in the left column (stacked vertically, one agent per row) and
uses `/api/agents` (writes need the Bearer write token) plus the read-only
`GET /api/agents/projects` (sorted `jira_project_key` values). It polls every 30 s (the aiAgent default poll
interval). A card shows Disabled when not enabled (the card has no Enabled checkbox; enable or disable an agent via Edit -> Enabled -> Save), otherwise Offline when there is no status or the heartbeat is
older than 90 s (3 polls), else Idle / `Running on KEY` / Error. The Disabled badge is yellow and the Offline badge is red; the other statuses keep the neutral badge. While Running (and not Offline) — i.e. only while the capability holds an issue claim and is working on it, not merely triggered or polling — the capability chip matching `status.currentCapability` is highlighted (added if not enabled), and the whole card gets a highlighted blue border (`data-working="true"`); Disabled, Offline, Idle and Error cards keep the normal border. `heartbeatAt` is treated as UTC (the heartbeat
writer must store UTC); "now" comes from the response `Date` header. The write token is asked for in the page and
remembered in the `dashboard_write_token` cookie (`path=/; SameSite=Strict`, `Secure` on HTTPS, 30-day max-age) and shared by all write forms; a 401 clears the cookie and brings the prompt back. The cookie is set by JavaScript so it cannot be `HttpOnly`: any XSS can read it, and the browser also sends it to the server on every request (the server only authenticates the `Authorization: Bearer` header). No write request is sent without a token.
There is no frontend test runner; API tests are in `tests/AgentEndpointsTests.cs`.

Top 5 AI tasks cards get a highlighted blue border (same as working agent cards) while an
enabled agent with a fresh heartbeat is Running on that issue in the Planning, Builder or Review
capability (agents are polled every 30 seconds).

Top 5 AI tasks cards show a yellow badge (text = label name) next to the status for each Jira label
`ai-approved`, `aiagent-needs-clarification`, and `aiagent-ready-to-merge` on the issue (exact match,
`data-testid="ai-task-label-badge"`). The Review capability adds the `aiagent-ready-to-merge` label while all
review threads are resolved, and removes it when unresolved or failed review work remains.
Manual check: add `ai-approved` to an active AI issue in Jira and confirm the yellow badge appears within one
30-second poll and disappears when removed; repeat with `aiagent-needs-clarification` and
`aiagent-ready-to-merge`; check light and dark themes.

Manual check: cards appear above the task editor; create via `+`; edit; toggle enable/disable; delete is only available
in the edit form (Edit → Delete → inline Confirm/Cancel) and is not shown on cards or in the create form; status changes within one poll after updating `agent_status`; Offline after a stale
heartbeat; setting `agent_status.state` to Running with a fresh heartbeat highlights the card border within one poll, while a stale heartbeat or disabling the agent removes it; the Network tab shows no write without a token, and a bad token gives 401 and asks again.

The connection is read from the standard DarkFactor `AppSettings` configuration (`DatabaseConnection`).
`DarkFactor.Common.Lib` is published to the private `DarkFactor` NuGet feed, which must be configured
for restore to succeed.

The centre panel list of tasks not yet sent to Jira (`status=New`) refreshes every 30 seconds, so tasks
created in Jira disappear without a page reload; an open edit form keeps its input across refreshes.

## AI vs non-AI details

In the "Tasks solved per project" panel, the "AI vs non-AI per project" list and bar chart are collapsed by
default. The chevron toggle next to the heading expands and collapses them; the state is not persisted.

## Theme

The header has a Theme selector (System, Light, Dark). System, the default, follows the OS/browser
`prefers-color-scheme` and updates live. The choice is stored only in the browser's `localStorage`
(`aidashboard.theme`) and is never sent to the server.

The "Tasks solved per project" panel is hidden by default. The bar-chart icon at the left of the header opens it as
an overlay drawer on top of the page (it stays mounted, so its state is kept); close it with the × button, the
backdrop or Escape.
