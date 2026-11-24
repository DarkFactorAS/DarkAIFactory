# aidashboard

ASP.NET Core web project serving a React + Tailwind (Vite) dashboard shell with a
header titled "DarkAIFactory - AI Team workflow" (the earlier
"DarkAIFactoy" typo from DFAI-30 was fixed in DFAI-49), centred. It also exposes `GET /api/health`,
`GET /api/tasks/active-ai`, and `GET /api/stats/solved-by-project`, which
returns `[{"project": "DFAI", "solvedCount": 3, "aiCount": 1, "nonAiCount": 2}]`
counting Jira issues in the Done status category per project (limited to the projects builders pick up: `agent_projects` of enabled, non-deleted agents with the Builder capability, joined to `manager_projects`; all `manager_projects` when no agents are defined), split into `aiCount` (issues carrying a configured AI workflow pickup or analyzed label) and `nonAiCount` (502 with a generic error if Jira fails;
credentials are never logged).

`GET /api/tasks/active-ai` returns at most 5 Jira issues (`key`, `summary`,
`status`, `description`, `labels`) in the configured project. Labels are not used as a filter;
the only filter is the configured active statuses (`Dashboard.ActiveStatuses`, set through the
cogwheel; an empty list means every non-Done status). Ordering is most recently
updated first, ties broken by key.
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
`npm install && npm run dev` in `aiteam/aidashboard` (port 5173, proxies `/api` to localhost:5000; override
with `AIDASHBOARD_DEV_PORT` and `AIDASHBOARD_API_URL`). In VS Code, the "aidashboard: Full stack" launch
configuration starts the API on 8282 and Vite on 8281 with the debugger attached.
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
`/api/stats/solved-by-project`, plus two writes: `POST /api/tasks/{key}/approve` (see below) and `POST /api/tasks/{key}/comments` (`text`) appends the text
as a new Jira comment on that issue (the description is never changed; 400 on invalid input,
502 if Jira fails or times out); its GitHub token can remain empty.
Like every write endpoint, comment submissions require a logged-in session (see [Login](#login)).
Only issues currently returned by the active AI task list in the configured
Jira projects are authorized (403 otherwise).
`GET /api/tasks/{key}/comments` (public, like the card description) returns the 3 newest Jira comments of an
active issue as `[{ author, created, body }]`, newest first, with the body converted to plain text (400 invalid key,
403 not active, 502 on Jira failure). Each "Top 5 AI tasks" card has a **Comments** button
(`ai-task-history-button`) that loads and toggles these comments (`ai-task-history`); "No comments" is shown when empty.
Manual check: add comments to an active issue, click Comments and see the latest 3 newest first; an issue
without comments shows "No comments"; it works logged out; check the light and dark themes.
`POST /api/tasks/{key}/approve` (login required) adds the approved label (`workflow_labels[aiagent, ApprovedLabel]`,
default `aiagent-approved`) to an active issue that has the approval label (`ApprovalLabel`, default
`aiagent-awaiting-approval`) so the agent continues; it changes only the labels field and the agent removes both labels.
400 invalid key, 403 issue not active, 409 no approval label, 502 Jira failure; 200 without a Jira call if already approved.
Any logged-in user may approve. The "Top 5 AI tasks" cards show an **Approve** button for logged-in users when the issue has
the approval label and not the approved label.
Manual check: add `aiagent-awaiting-approval` to an active issue; logged in, the button appears within one 30 s poll;
click it: the issue gets `aiagent-approved` and, after the agent runs, both labels are removed. Logged out: no button.
Check light and dark themes. Tip: add `aiagent-awaiting-approval` to the card badge labels to see it on the card.
`POST /api/tasks/{key}/merge` (login required) requests the merge of a ready pull request by adding the merge-requested label
(`workflow_labels[aiagent, MergeRequestedLabel]`, default `aiagent-merge-requested`) to an active issue that has the
`aiagent-ready-to-merge` label (also set by the Review capability on reviewer approval, "Approval recommended") and not the `aiagent-failed` label (`FailureLabel`). 400 invalid key, 403 issue not active,
409 not ready to merge or failed, 502 Jira failure (generic error); 200 without a Jira call if the label is already present.
The dashboard never calls GitHub. The "Top 5 AI tasks" cards show a **Merge** button for logged-in users when the issue has the
ready-to-merge label and neither the merge-requested nor the failed label; after the click (or when the label is present) the
card shows "Merge requested". A 401 opens the login form as for Approve.
Manual check: add `aiagent-ready-to-merge` to an active issue; logged in, the Merge button appears within one 30 s poll; click it:
the issue gets `aiagent-merge-requested` and the card shows "Merge requested". Logged out: no button. Adding `aiagent-failed`
hides it. Check light and dark themes.
`POST /api/tasks/{key}/retry` (login required) clears **all** labels on an active issue that has the `aiagent-failed` label
(`workflow_labels[aiagent, FailureLabel]`) by setting the Jira `labels` field to an empty array; this also removes non-AI labels
and any `aiagent-pr-tracking-pending` label. 400 invalid key, 403 issue not active, 409 issue has no failure label, 502 Jira
failure (generic error). The "Active AI tasks" cards show a red **Retry** button for logged-in users when the issue has the failure label.
Manual check: logged in, add `aiagent-failed` to an active issue; the red Retry button appears; click it: all labels are removed
in Jira and the card refreshes without the button. Logged out: no button. Check light and dark themes.

`GET /api/tasks/config` also returns `approvalLabel`, `approvedLabel`, `mergeRequestedLabel` and `readyToMergeLabel`, and returns the non-secret `projectKeys` from the `manager_projects` table and `projectKey` (the first one) used as the default by task creation.
The New task form has a project selector fed by `projectKeys`, defaulting to the first key; with only one project it shows the key as read-only text instead. The chosen project is shown as a badge in the pending task list and cannot be changed when editing. `POST`/`PUT /api/agent-tasks` reject a `projectKey` that is not in `manager_projects` (400).
AI labels: `GET /api/settings/ai-labels` returns `{ labels }`; `PUT /api/settings/ai-labels` (`labels`, login
required, 401 otherwise) validates (max 20 labels, 1-255 chars, no whitespace or commas; 400 otherwise), writes
only `settings[aidashboard, Dashboard.AiLabels]` and updates the running app immediately, no restart needed. An empty
list means: use the workflow pickup/analyzed labels. The "Edit AI labels" button in the "Tasks solved per project"
panel uses it and redraws the graph.

Active statuses: `GET /api/settings/active-statuses` returns `{ statuses }`; `PUT /api/settings/active-statuses`
(`statuses`, login required, 401 otherwise) validates (max 20 statuses, 1-255 chars, no commas; spaces
are allowed; 400 otherwise), writes only `settings[aidashboard, Dashboard.ActiveStatuses]` and updates the running app
immediately. An empty list means: show every non-Done status. The cogwheel button in the "Top 5 AI tasks" header
opens the editor and reloads the panel after saving.

Card labels: `GET /api/settings/card-labels` returns `{ labels }`; `PUT /api/settings/card-labels` (`labels`,
login required, 401 otherwise) validates like the AI labels (max 20, 1-255 chars, no whitespace or commas; 400
otherwise), writes only `settings[aidashboard, Dashboard.CardLabels]` and updates the running app immediately.
The same cogwheel editor has a "Labels to show on cards" field. An empty list means the defaults below.

Manual check (cogwheel): open the cogwheel, save `In Progress` and the list narrows without a reload;
an empty value restores all non-Done statuses; saving with an expired session opens the login page; the list survives a reload.
See the configuration guide for migrating existing credentials into the database.

Task API (table `agent_tasks`, created only by dbmigration): `GET /api/agent-tasks[?status=]`,
`GET /api/agent-tasks/{id}`, `POST /api/agent-tasks` (`projectKey`, `title`, `description`;
created as `New`) and `PUT /api/agent-tasks/{id}` (also `status` New/Created/Failed and
optional `jiraKey`). Invalid input returns 400, unknown ids 404.
The dashboard editor instead uses `PATCH /api/agent-tasks/{id}` (`title`, `description`),
which atomically edits only tasks still in `New` status, preserves workflow fields,
and returns 409 if the task has progressed.

Agent slots UI (`js/AgentSlots.jsx`, `js/AgentEditor.jsx`, `js/agentApi.js`) sits in the left column (stacked vertically, one agent per row) and
uses `/api/agents` plus the read-only
`GET /api/agents/projects` (sorted `jira_project_key` values). It polls every 30 s (the aiAgent default poll
interval). A card shows Disabled when not enabled (the card has no Enabled checkbox; enable or disable an agent via Edit -> Enabled -> Save), otherwise Offline when there is no status or the heartbeat is
older than 90 s (3 polls), else Idle / the Jira key (or Running without a key) / Error. Idle shows no badge. The capability chips sit in the header row next to the agent name, before the badge. The Disabled badge is yellow and the Offline badge is red; the other statuses keep the neutral badge. While Running (and not Offline) — i.e. only while the capability holds an issue claim and is working on it, not merely triggered or polling — the capability chip matching `status.currentCapability` is highlighted (added if not enabled), and the whole card gets a highlighted blue border (`data-working="true"`); Disabled, Offline, Idle and Error cards keep the normal border. `heartbeatAt` is treated as UTC (the heartbeat
writer must store UTC); "now" comes from the response `Date` header.

Model status orbs (`js/ModelOrbs.jsx`, `js/Orb.jsx`, DFAI-246) sit in the centre column directly above the New task
form and are visible logged in or not. They port the Claude/Codex particle orbs from ThorDashboard and show one orb
per LLM provider (Claude, Copilot), driven by the agents in `/api/agents` (polled every 30 s): working (faster,
brighter; `(n)` when several agents run) when an agent with that `llmProvider` is Running, error (red) when an online
agent reports an error/failed state, waiting (amber) for another non-idle state, idle when at least one agent is
enabled and online, otherwise offline (grey). "Pause motion" (stored in localStorage `aidashboard.pauseMotion`) and
`prefers-reduced-motion` draw a single still frame.
The agent editor (edit via Edit, create via `+`) opens as a centred modal overlay (native `<dialog>`) above a dimmed
backdrop instead of below the agent list. Close it with the × button, a backdrop click, Escape or Cancel; all discard
unsaved input. Save and Delete close it too, and the 30 s poll does not reset it.
There is no frontend test runner; API tests are in `tests/AgentEndpointsTests.cs`.

Top 5 AI tasks cards get a highlighted blue border (same as working agent cards) while an
enabled agent with a fresh heartbeat is Running on that issue in the Planning, Builder or Review
capability (agents are polled every 30 seconds).

Top 5 AI tasks cards show a yellow badge (text = label name) next to the status for each configured
Jira label (`Dashboard.CardLabels`; default `ai-approved`, `aiagent-needs-clarification` and
`aiagent-ready-to-merge` when empty) that is on the issue (exact match,
`data-testid="ai-task-label-badge"`). The Review capability adds the `aiagent-ready-to-merge` label only after the
pull request has at least one review comment and all review threads are resolved, or when the reviewer's latest
review after the last non-merge commit contains the approval phrase ("Approval recommended", configurable via
`ApprovalPhrase`) and nothing is unresolved or failed. It removes the label when
unresolved or failed review work remains, and while the agent is fixing review comments.
Manual check: add `ai-approved` to an active AI issue in Jira and confirm the yellow badge appears within one
30-second poll and disappears when removed; repeat with `aiagent-needs-clarification` and
`aiagent-ready-to-merge`; check light and dark themes.

The Agents heading has an `Edit` toggle button (`data-testid="agent-edit-toggle"`, `aria-pressed`) shown only when logged in. It is off by default and not persisted (it resets on logout); the per-card Edit buttons appear only while it is on. The `+` Add agent tile is unaffected.

Manual check: cards appear above the task editor; the Edit buttons on cards only appear after turning the toggle on; create via `+` (the form opens as a modal overlay; close with ×, backdrop, Escape or Cancel); edit (also a modal overlay); toggle enable/disable; delete is only available
in the edit form (Edit → Delete → inline Confirm/Cancel) and is not shown on cards or in the create form; status changes within one poll after updating `agent_status`; Offline after a stale
heartbeat; setting `agent_status.state` to Running with a fresh heartbeat highlights the card border within one poll, while a stale heartbeat or disabling the agent removes it.

## HTTPS

Outside Development, Kestrel listens with TLS only, on port 8080 (`Https:Port` to change it), so
the Docker container and the production deploy (`--publish <port>:8080`) are reached at
`https://<host>:<port>`. Plain `http://` to that port does not work. `aiteam/docker-compose.yml`
sets `Https__Port: 443` and publishes it as 8281 (`https://localhost:8281`), because Docker
Desktop only links a port as `https://` when the container port is 443. The certificate comes from
`Kestrel:Certificates:Default` (for example the environment variables
`Kestrel__Certificates__Default__Path` and `Kestrel__Certificates__Default__Password` with a
mounted `.pfx`). Without one, the app generates a self-signed certificate at startup
(`Providers/DashboardHttps.cs`): traffic is encrypted, but browsers show a certificate warning
that has to be accepted once per start. Development (`dotnet run`, VS Code launch) keeps plain
HTTP on `ASPNETCORE_URLS` behind the Vite dev proxy.

## Login

The dashboard is public and read-only; making changes requires a DarkFactor account (DFAI-214). Login uses the same account
server and `DarkFactor.AccountClient.Lib` client as DFWeb and GameServer: the password is
encrypted with `DFCrypt.EncryptInput` and checked with `IAccountClient.LoginAccount`. Any account
the account server accepts may sign in. Configure the account server in the `AccountServer`
section of the appsettings (see [Config](Config/README.md)).

- `POST /api/auth/login` (`username`, `password`, optional `rememberMe`) returns `{ username }` and sets the
  `aidashboard_auth` session cookie (HttpOnly, `SameSite=Strict`, Secure outside Development; 12 hours,
  sliding). `rememberMe: true` makes it a 7-day sliding cookie; unchecked or missing keeps the 12-hour one. Login is accepted only when ASP.NET Core sees HTTPS outside Development (this app does not
  process forwarded-protocol headers), so the app serves HTTPS itself there; see [HTTPS](#https). 400 for missing input or an
  insecure request, 401 `Invalid username or password.` (same text for unknown user
  and wrong password), 502 if the account server fails, 503 if `AccountServer.Endpoint` is not set.
- `GET /api/auth/me` returns `{ username }`, or 401 when not logged in.
- `POST /api/auth/logout` ends the session (204).

Without a session every read (`GET`/`HEAD`) works, and every write (`POST`, `PUT`, `PATCH`,
`DELETE`) returns 401 (no redirect); `/api/auth/me` also needs a session. This is the fallback
authorization policy in `Providers/DashboardAuthentication.cs`, so new write endpoints are protected by
default.

The page checks `/api/auth/me`. Without a session it shows the dashboard read-only: the header shows
"Read-only" and a Log in button (where Log out is when logged in), and the editing controls are hidden
(the New task form, task and agent Edit buttons, Add agent, the statuses cogwheel, Edit AI labels, and
adding text to a Top 5 task). Log in opens the sign-in form (`js/LoginPage.jsx`); Back to dashboard
returns without logging in. Logged in, the header shows the user and a Log out button. A write that
returns 401 (for example an expired session) switches to read-only and opens the sign-in form with a
"session has expired" note (`js/auth.jsx`). The password and the account token are never stored or logged.
The earlier shared write token (`Dashboard__WriteToken`, `dashboard_write_token` cookie) was removed.

Session cookies are protected with ASP.NET Core Data Protection keys kept in the container, so a
restart or redeploy signs everyone out, including remembered 7-day logins.

Manual check: open the dashboard logged out and see all panels with data, "Read-only" and Log in in
the header, and no editing controls; Log in opens the form, Back to dashboard returns; a wrong password
shows the error and clears the password field; a valid account returns to the dashboard with the user
name, Log out and the editing controls; reload keeps the session; Log out returns to read-only, and a
`POST /api/agent-tasks` then returns 401; with "Remember me for 7 days" checked the cookie expires in about 7 days, unchecked about 12 hours; check light and dark themes. With two or more `manager_projects` rows the New task form lists every project and preselects the last chosen one (kept across reloads and server restarts; the default is used when the stored key is missing or unknown); creating a task in the second shows that key as the badge and stores it as `project_key`; with one project no dropdown appears and the key is shown as text.

The connection is read from the standard DarkFactor `AppSettings` configuration (`DatabaseConnection`).
`DarkFactor.Common.Lib` is published to the private `DarkFactor` NuGet feed, which must be configured
for restore to succeed.

The centre panel list of tasks not yet sent to Jira (`status=New`) refreshes every 30 seconds, so tasks
created in Jira disappear without a page reload; an open edit form keeps its input across refreshes.

## AI vs non-AI details

In the "Tasks solved per project" panel, the "AI vs non-AI per project" list and bar chart are collapsed by
default. The chevron toggle next to the heading expands and collapses them; the state is not persisted.

## Theme

The last project chosen in the New task form is remembered as a preference in the browser's localStorage (`aidashboard.projectKey`); the preference itself is not synced to the server, but the selected project key is sent to the server as part of the request body when a task is created.

The header has a Theme selector (System, Light, Dark). System, the default, follows the OS/browser
`prefers-color-scheme` and updates live. The choice is stored only in the browser's `localStorage`
(`aidashboard.theme`) and is never sent to the server.

The "Tasks solved per project" panel is hidden by default. The bar-chart icon at the left of the header opens it as
an overlay drawer on top of the page (it stays mounted, so its state is kept); close it with the × button, the
backdrop or Escape.

## Agent metrics

`GET /api/agents/metrics` (public read) returns one entry per non-deleted agent:
`[{"agentId": 1, "allTime": {...}, "last7Days": {...}}]`. Both windows aggregate `agent_runs`
(`last7Days` = `started_at >= UTC now - 7 days`) and contain `succeeded`, `failed` (runs still in
progress count as neither), `wallClockMs`/`avgWallClockMs` (`finished_at - started_at`),
`llmDurationMs`/`avgLlmDurationMs`, `inputTokens`, `outputTokens`, `toolCalls` and `costUsd`.
A value that no run reported is `null` (shown as "N/A"), never 0. Averages cover the runs that reported the value.
The route is registered before `/api/agents/{id:long}` and does not clash with it.

Each agent card (`js/AgentMetrics.jsx`) shows a compact row with an All / 7d toggle in front of "x/(x+y) ok" (succeeded/total finished runs),
wall-clock time and cost. An info icon after the ok text shows, on hover or focus, the tokens
(input/output/total), tool calls, averages and LLM vs wall-clock time. Metrics refresh on the 30 s agent poll.
The JS bundle is built with `npm install && npm run build` in `aiteam/aidashboard` (output `dist`, git-ignored).
