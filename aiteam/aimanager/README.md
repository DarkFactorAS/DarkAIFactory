# Jira Manager Agent

This Dockerized worker polls Jira for eligible tasks, clones the configured
GitHub repository and its submodules into a private temporary workspace, asks
GitHub Copilot CLI to prepare an implementation plan, posts the plan to Jira,
then implements the task and pushes the changes to a new GitHub branch before
posting the result.

It reads Jira project keys and target repository URLs from `manager_projects`
in the database. For each project it watches status `Selected for Development`
and skips epics by default. Credentials and status/label settings are shared
across all projects on the configured Jira instance.

## Project mappings

Startup creates `manager_projects` with an auto-incrementing `BIGINT` primary
key `id`, a unique `jira_project_key` and a `target_repository_url`.
Populate it after the first startup, for example:

```sql
INSERT INTO manager_projects (jira_project_key, target_repository_url)
VALUES
    ('DFAI', 'https://github.com/owner/first-repository.git'),
    ('BOTS', 'https://github.com/owner/second-repository.git');
```

Each Jira project maps to one HTTPS GitHub repository; multiple projects may
map to the same repository. The manager reloads mappings in ID order every
polling cycle and processes each project's tasks using its mapped repository
for cloning, pushing and pull requests. Changes take effect on the next cycle
without restarting. An empty table is logged and the manager waits for mappings;
there is no fallback to `JIRA_PROJECT_KEY` or `TARGET_REPOSITORY_URL`.
Invalid mappings or database read failures stop the manager rather than silently
processing tasks against another repository. A Jira polling failure is logged
and does not prevent the remaining projects from being polled in that cycle.

`JIRA_JQL` can override the task filter and ordering, but the manager always
adds the mapped project constraint and pickup-label exclusion. Use a filter
without a fixed project (for example `status = "Ready" ORDER BY created ASC`)
to apply it across all mapped projects.

## Database patcher

Before checking Copilot availability or polling Jira, the manager waits for the
database connection and runs `AiDatabasePatcher`, derived from
`DarkFactor.Common.Lib`'s `StartupDatabasePatcher`, just like aibuildpage.
It applies the base patches and the project-mapping table migration. Patching failure is logged and
stops the manager with exit code 1.

Copy `aiteam/config/aimanager/appsettings.Production.json.example` to
`aiteam/config/aimanager/appsettings.Production.json` and set
`DatabaseConnection` to match the MariaDB credentials in `aiteam/.env`.
The runtime configuration is ignored by Git. Compose mounts this directory
read-only at `/app/Config`; override `AIMANAGER_CONFIG_PATH` for another directory.
For local runs, the shared configuration helper reads
`Config/appsettings.<DOTNET_ENVIRONMENT>.json` relative to the working directory
(`Production` by default). Create that directory/file before running locally.

Restore requires `NUGET_USERNAME` and `NUGET_TOKEN` for the private
`DarkFactorAS` NuGet feed. Docker builds receive them through BuildKit secrets,
not build arguments; set them in `aiteam/.env` for Compose. For a direct build:

```sh
docker build -f aiteam/aimanager/Dockerfile \
  --secret id=nuget_username,env=NUGET_USERNAME \
  --secret id=nuget_token,env=NUGET_TOKEN .
```

## Run

1. Copy `aiteam/.env.example` to `aiteam/.env`.
2. Set `JIRA_BASE_URL`, `JIRA_EMAIL`, and `JIRA_API_TOKEN`.
3. Set `GH_TOKEN` to a fine-grained GitHub personal access token with only the
   **Copilot Requests** permission.
4. Set `GITHUB_REPOSITORY_TOKEN` to a separate fine-grained token with
   **Contents: read and write** and **Pull requests: read and write**
   permissions for every mapped repository. The worker uses this token only to
   clone, push its branch and open the pull request.
5. Configure the database as described above and populate `manager_projects`
   after the manager's first startup. `TARGET_REPOSITORY_URL` and `JIRA_PROJECT_KEY`
   in the shared `.env` are only used by aiworker, not aimanager.
6. Set `MARIADB_ROOT_PASSWORD` and `MARIADB_PASSWORD`. Optionally change
   `MARIADB_DATABASE`, `MARIADB_USER` and `MARIADB_VOLUME_NAME`.
7. The MariaDB data volume is external, so Compose does not create it. Create
   it once before the first start:

   ```sh
   docker volume create aiteam_mariadb_data
   ```

   (use the value of `MARIADB_VOLUME_NAME` if you changed it). Port 3306 is not
   published; other services reach the database at host `aiteam_database`.
8. From the `aiteam` directory, start the worker with:

   ```sh
   docker compose --env-file .env -f docker-compose.yml up --build -d
   ```

The Compose file and `.env` are in `aiteam/`; the Docker build context is
the repository root (the Dockerfile is `aiteam/aimanager/Dockerfile`, as used by CI). For each issue, the worker clones the repository to an
private temporary workspace, creates a branch named `ai-agent/<issue>-<id>`,
and pushes a commit if the agent made changes. Successfully delivered workspaces
are removed. Workspaces for failed or interrupted tasks are retained for
recovery; their paths are written to the worker log, and Jira failure comments
identify any branch that was already pushed. When work starts, the worker moves
the issue to `In Progress` (a failure to do so is logged and commented, and work
continues). After pushing, it opens a pull request against the repository's
default branch, links it in the Jira result comment and moves the issue to
`In Review`. If the agent made no changes, no pull request is created and the
status is left unchanged. Copilot cannot execute shell commands, so it cannot run tests; validate
the pushed branch separately. The worker follows repository Markdown
instructions, including applicable `AGENTS.md` and Git workflow rules.

Issues retain the `ai-agent-picked` label after processing. If processing fails,
the worker also adds `ai-agent-failed` and posts an error comment. For failures
before a branch is pushed, the worker attempts to restore the issue's original
status; after resolving the failure, remove the pickup label to retry. Verify
the status manually if restoration fails. If a branch was already pushed, follow
the stage-specific recovery instructions in the Jira comment instead of removing
the pickup label and rerunning the implementation.

## Run from Visual Studio Code

Open the repository root in VS Code, copy `aiteam/.env.example` to
`aiteam/.env`, and set the Jira and GitHub credentials there. Configure
repository URLs and Jira project keys in `manager_projects`.
Install the C# extension, then select **AI Worker (.NET)** in Run and Debug and
press F5. VS Code builds the worker first and loads `aiteam/.env`.

The **build aimanager** task is also available from **Terminal > Run Build Task**.

## Tests

Unit tests (xunit) and their `ManagerAgent.Tests.csproj` project live in
`src/ManagerAgentTests/`. They cover configuration loading, the Jira client (with a stubbed
HTTP handler), prompt building and base database patching success/failure.
Run them with:

```sh
dotnet test aiteam/aimanager/src/ManagerAgentTests/ManagerAgent.Tests.csproj
```

The **test aimanager** VS Code task runs the same command. The tests are not
included in the Docker image.

The `Manager-CI-DotNet` workflow builds the manager and runs these tests in
Release configuration on pull requests and pushes to `main`.

## Configuration

| Variable | Default | Description |
| --- | --- | --- |
| `JIRA_BASE_URL` | required | HTTPS Jira Cloud base URL |
| `JIRA_EMAIL` | required | Jira account email |
| `JIRA_API_TOKEN` | required | Jira API token |
| `GH_TOKEN` | required | Fine-grained token with Copilot Requests permission |
| `GITHUB_REPOSITORY_TOKEN` | required | Fine-grained token with Contents and Pull requests read/write access to all mapped repositories |
| `JIRA_STATUS_NAME` | `Selected for Development` | Status used by the default JQL |
| `JIRA_JQL` | generated from project/status | Optional task filter/ordering override, always constrained to each mapped project |
| `JIRA_PICKUP_LABEL` | `ai-agent-picked` | Label used to avoid repeat pickup |
| `JIRA_FAILURE_LABEL` | `ai-agent-failed` | Label added when processing fails |
| `JIRA_IN_PROGRESS_STATUS` | `In Progress` | Status the issue is moved to when work starts |
| `JIRA_IN_REVIEW_STATUS` | `In Review` | Status the issue is moved to after the pull request is created |
| `POLL_INTERVAL_SECONDS` | `30` | Poll interval (minimum 5 seconds) |
