# Jira Manager Agent

aimanager polls Jira, clones each mapped repository for read-only context, asks
Copilot CLI for an implementation plan, saves the full plan in MariaDB and posts
it to Jira. It does not implement code, run builds/tests in the cloned repository,
create branches, commit, push, open pull requests or change ticket statuses.
Copilot shell and write tools are always disabled.

## Database

On startup `AiDatabasePatcher` runs the shared `StartupDatabasePatcher` and creates:

- `manager_projects`: auto-increment `BIGINT` primary key `id`, unique
  `jira_project_key`, and `target_repository_url`.
- `manager_plans`: auto-increment `BIGINT` primary key `id`, `manager_project_id`
  foreign key, unique `jira_key`, full `plan` (`LONGTEXT`), `posted_to_jira`
  flag and `created` timestamp.

The manager waits for a database connection and stops with exit code 1 if
patching fails. Copy `aiteam/config/aimanager/appsettings.Production.json.example`
to `aiteam/config/aimanager/appsettings.Production.json` and set
`DatabaseConnection` to match MariaDB credentials in `aiteam/.env`.
The runtime configuration is ignored by Git. Compose mounts this directory
read-only at `/app/Config`; override `AIMANAGER_CONFIG_PATH` for another directory.
For local runs, the shared helper reads
`Config/appsettings.<DOTNET_ENVIRONMENT>.json` relative to the working directory
(`Production` by default).

Populate the mappings after the first startup:

```sql
INSERT INTO manager_projects (jira_project_key, target_repository_url)
VALUES
    ('DFAI', 'https://github.com/owner/first-repository.git'),
    ('BOTS', 'https://github.com/owner/second-repository.git');
```

Mappings reload in ID order every polling cycle. Each Jira project maps to one
HTTPS GitHub repository; multiple projects may share a repository. Empty mappings
are logged and the manager waits. Invalid mappings or database read failures
stop the manager. A Jira polling failure does not prevent polling other projects.
The manager ignores environment `JIRA_PROJECT_KEY` and `TARGET_REPOSITORY_URL`;
those settings remain available to aiworker.

## Planning and recovery

For each eligible ticket, the manager creates a temporary repository checkout,
generates a plan without changing files and removes the checkout. It saves the
plan in `manager_plans` **before** posting to Jira, marks it as posted after all
comments succeed, then adds `ai-manager-planned`. Long plans are split into
numbered Jira comments without truncation.

A saved plan is reused on retries, so a Jira or label failure does not trigger
another agent run. If posting fails partway through a multi-comment plan, retry
may duplicate comments already posted. Database and Jira writes are not a
single transaction. Failures are logged and reported to Jira with
`ai-manager-failed`; incomplete tickets are retried next cycle. Plans already
posted to Jira are not posted again when only the completion label needs retrying.

Manager labels are distinct from aiworker's pickup labels, leaving planned
tickets available for implementation. Ticket statuses remain unchanged.
To deliberately replan a ticket, delete its `manager_plans` row and remove its
manager completion label in Jira.

## Run

1. Copy `aiteam/.env.example` to `aiteam/.env`.
2. Set `JIRA_BASE_URL`, `JIRA_EMAIL`, and `JIRA_API_TOKEN`.
3. Set `GH_TOKEN` to a fine-grained token with **Copilot Requests** permission.
4. Set `GITHUB_REPOSITORY_TOKEN` with **Contents: read** permission for all mapped
   repositories and their submodules. The manager needs no push or pull-request
   permissions. Use a separate read-only token from aiworker when running both.
5. Configure the database file above, and set `MARIADB_ROOT_PASSWORD`,
   `MARIADB_PASSWORD`, `NUGET_USERNAME` and `NUGET_TOKEN` in `aiteam/.env`.
6. Create the external database volume once:

   ```sh
   docker volume create aiteam_mariadb_data
   ```

7. From `aiteam`, run:

   ```sh
   docker compose --env-file .env up --build -d manager-agent
   ```

Compose also starts MariaDB. Populate `manager_projects` after startup.
The Docker build context is the repository root. The private `DarkFactorAS`
NuGet feed requires `NUGET_USERNAME` and `NUGET_TOKEN`; Docker receives these
through BuildKit secrets, not build arguments:

```sh
docker build -f aiteam/aimanager/Dockerfile \
  --secret id=nuget_username,env=NUGET_USERNAME \
  --secret id=nuget_token,env=NUGET_TOKEN .
```

For local execution, provide the database configuration and environment variables,
then run `dotnet run --project aiteam/aimanager/src/ManagerAgent/ManagerAgent.csproj`.

## Tests

Unit tests and their project live in `src/ManagerAgentTests/`:

```sh
dotnet test aiteam/aimanager/src/ManagerAgentTests/ManagerAgent.Tests.csproj
```

The **test aimanager** VS Code task runs the same command. Tests are not in the
Docker image. `Manager-CI-DotNet` builds the manager and runs unit tests in Release
on pull requests and pushes to `main`; this validates aimanager itself, not code
from Jira tickets.

## Configuration

| Variable | Default | Description |
| --- | --- | --- |
| `JIRA_BASE_URL` | required | Shared HTTPS Jira instance |
| `JIRA_EMAIL` | required | Jira account email |
| `JIRA_API_TOKEN` | required | Jira API token |
| `GH_TOKEN` | required | Token with Copilot Requests permission |
| `GITHUB_REPOSITORY_TOKEN` | required | Read-only access to all mapped repositories |
| `JIRA_STATUS_NAME` | `Selected for Development` | Eligible ticket status |
| `JIRA_JQL` | generated | Optional filter/ordering, constrained to each mapped project |
| `JIRA_PICKUP_LABEL` | `ai-manager-planned` | Completion label; Compose uses `MANAGER_JIRA_PICKUP_LABEL` |
| `JIRA_FAILURE_LABEL` | `ai-manager-failed` | Failure label; Compose uses `MANAGER_JIRA_FAILURE_LABEL` |
| `POLL_INTERVAL_SECONDS` | `30` | Poll interval, minimum 5 seconds |
