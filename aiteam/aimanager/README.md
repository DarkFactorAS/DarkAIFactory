# Jira Manager Agent

aimanager polls Jira, clones each mapped repository for read-only context, asks
Copilot CLI for an implementation plan, saves the full plan in MariaDB and posts
it to Jira. It does not implement code, run builds/tests in the cloned repository,
create branches, commit, push, open pull requests or change ticket statuses.
Copilot shell and write tools are always disabled.

## Database

The standalone [dbmigration](../dbmigration/README.md) executable creates:

- `manager_projects`: auto-increment `BIGINT` primary key `id`, unique
  `jira_project_key`, and `target_repository_url`.
- `manager_plans`: auto-increment `BIGINT` primary key `id`, `manager_project_id`
  foreign key, unique `jira_key`, full `plan` (`LONGTEXT`), `posted_to_jira`
  flag and `created` timestamp.

The manager does not create tables or run migrations. Run dbmigration first;
missing tables or database read failures stop startup.
Copy `aiteam/config/aimanager/appsettings.Production.json.example`
to `aiteam/config/aimanager/appsettings.Production.json` and set
`DatabaseConnection` to match MariaDB credentials in `aiteam/.env`.
The runtime configuration is ignored by Git. Compose mounts this directory
read-only at `/app/Config`; override `AIMANAGER_CONFIG_PATH` for another directory.
For local runs, configured JSON files are copied into the executable's `Config`
directory. See [App configuration](../config/README.md) for Production,
Development and DevelopmentDocker setup. Startup selects the executable directory
as its working directory. Jira/GitHub settings load from the shared `settings`
table, with three client-specific rows; see the configuration guide for migration.

Populate the mappings after running dbmigration:

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
Polling logs announce each distinct project mapping only once per process,
including newly added or changed mappings. The empty-project waiting message
is also printed only once. Polling continues every cycle and errors are still reported.
The manager reads project keys and repository URLs only from `manager_projects`;
aiworker uses shared database settings `Jira.JiraProjectKey` and `GitHub.TargetRepositoryUrl`.

## Planning and recovery

For each eligible ticket, the manager creates a temporary repository checkout,
generates a plan without changing files and removes the checkout. It saves the
plan in `manager_plans` **before** posting to Jira, marks it as posted after all
comments succeed, then adds the configured analysis label (`Jira.JiraAnalyzedLabel`,
default `ai-analyzed`) and the configured completion label
(`ai-manager-planned` by default). Long plans are split into
numbered Jira comments without truncation.

A saved plan is reused on retries, so a Jira or label failure does not trigger
another agent run. If posting fails partway through a multi-comment plan, retry
may duplicate comments already posted. Database and Jira writes are not a
single transaction. Failures are logged and reported to Jira with
the shared failure label (`ai-agent-failed` by default); incomplete tickets are retried next cycle. Plans already
posted to Jira are not posted again when only the analysis or completion label
needs retrying. The analysis label is added even with a custom completion label.

Manager labels are distinct from aiworker's pickup labels, leaving planned
tickets available for implementation. Ticket statuses remain unchanged.
To deliberately replan a ticket, delete its `manager_plans` row and remove its
manager completion label in Jira.

## Run

1. Copy `aiteam/.env.example` to `aiteam/.env`.
2. In the database `settings` table, set shared `Jira.JiraBaseUrl`, `Jira.JiraEmail`, and `Jira.JiraApiToken`.
3. Set shared `GitHub.AgentGitHubToken` to a fine-grained token with **Copilot Requests** permission.
4. Set the `aimanager` row `GitHub.GitHubRepositoryToken` with **Contents: read** permission for all mapped
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

Compose starts MariaDB and runs dbmigration before starting the manager.
For initial setup, run the migration service separately, populate settings and
`manager_projects`, then start the manager as described in the configuration guide.
The Docker build context is the repository root. The private `DarkFactorAS`
NuGet feed requires `NUGET_USERNAME` and `NUGET_TOKEN`; Docker receives these
through BuildKit secrets, not build arguments:

```sh
docker build -f aiteam/aimanager/Dockerfile \
  --secret id=nuget_username,env=NUGET_USERNAME \
  --secret id=nuget_token,env=NUGET_TOKEN .
```

For local execution, run dbmigration first, configure the database connection in JSON and Jira/GitHub settings in the database,
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

All Jira/GitHub entries below are database setting names; only
`PollIntervalSeconds` remains in JSON.

| Setting | Default | Description |
| --- | --- | --- |
| `Jira.JiraBaseUrl` | required | Shared HTTPS Jira instance |
| `Jira.JiraEmail` | required | Jira account email |
| `Jira.JiraApiToken` | required | Jira API token |
| `GitHub.AgentGitHubToken` | required | Token with Copilot Requests permission |
| `GitHub.GitHubRepositoryToken` | required | Read-only access to all mapped repositories |
| `Jira.JiraStatusName` | `Selected for Development` | Eligible ticket status |
| `Jira.JiraJql` | generated | Optional filter/ordering, constrained to each mapped project |
| `Jira.JiraPickupLabel` | `ai-manager-planned` | Completion label |
| `Jira.JiraFailureLabel` | `ai-agent-failed` | Shared failure label |
| `Jira.JiraAnalyzedLabel` | `ai-analyzed` | Label added after the plan is posted to Jira |
| `PollIntervalSeconds` | `30` | Poll interval, minimum 5 seconds |
