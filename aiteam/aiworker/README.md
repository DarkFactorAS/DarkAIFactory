# Jira Coding Agent

This Dockerized worker polls Jira for eligible tasks, clones the configured
GitHub repository and its submodules into a private temporary workspace, asks
GitHub Copilot CLI to prepare an internal implementation plan without posting it
to Jira, then implements the task and pushes the changes to a new GitHub branch before
posting the result.

By default, it watches project `DFAI` in status `Selected for Development` and
skips epics. Set `Jira.JiraJql` to use a different query.

## Run

First configure the app's environment-specific JSON file as described in
[App configuration](../config/README.md). Jira/GitHub settings come from the shared
database `settings` table, with three client-specific rows. Docker mounts
the shared `aiteam/config` directory at `/app/Config` (`AITEAM_CONFIG_PATH` overrides it). The worker now requires a database
connection to an already migrated database. It never creates tables.
Run [dbmigration](../dbmigration/README.md) before starting the worker.

1. Copy `aiteam/.env.example` to `aiteam/.env`.
2. In the database `settings` table, set shared `Jira.JiraBaseUrl`, `Jira.JiraEmail`, and `Jira.JiraApiToken`.
3. Set shared `GitHub.AgentGitHubToken` to a fine-grained GitHub personal access token with only the
   **Copilot Requests** permission.
4. Set the `aiworker` row `GitHub.GitHubRepositoryToken` to a separate fine-grained token with
   **Contents: read and write** and **Pull requests: read and write**
   permissions for the target repository. The worker uses this token only to
   clone, push its branch and open the pull request.
5. Set shared `GitHub.TargetRepositoryUrl` to an HTTPS GitHub repository URL, such as
   `https://github.com/owner/repository.git`.
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
the repository root (the Dockerfile is `aiteam/aiworker/Dockerfile`, as used by CI).
Compose runs dbmigration successfully before starting the worker.
For initial setup, run the migration service separately and populate its seeded
settings before starting the worker, as described in the configuration guide.
For each issue, the worker clones the repository to an
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
`aiteam/.env` for deployment settings, configure the database connection in
`aiteam/config/appsettings.Development.json`, and populate Jira/GitHub
credentials and repository URL in the database `settings` table.
Install the C# extension, then select **AI Worker (.NET)** in Run and Debug and
press F5. VS Code builds the worker first and loads `aiteam/.env`.

The **build aiworker** task is also available from **Terminal > Run Build Task**.

## Tests

Unit tests (xunit) live in `CodingAgent.Tests.csproj` and the `*Tests.cs` files
next to it. They cover configuration loading, the Jira client (with a stubbed
HTTP handler) and prompt building. Run them with:

```sh
dotnet test aiteam/aiworker/CodingAgent.Tests.csproj
```

The **test aiworker** VS Code task runs the same command. The tests are not
included in the Docker image.

## Configuration

All Jira/GitHub entries below are database setting names; only
`PollIntervalSeconds` remains in JSON.

| Setting | Default | Description |
| --- | --- | --- |
| `Jira.JiraBaseUrl` | required | HTTPS Jira Cloud base URL |
| `Jira.JiraEmail` | required | Jira account email |
| `Jira.JiraApiToken` | required | Jira API token |
| `GitHub.AgentGitHubToken` | required | Fine-grained token with Copilot Requests permission |
| `GitHub.GitHubRepositoryToken` | required | Fine-grained token with Contents and Pull requests read/write access to the target repository |
| `GitHub.TargetRepositoryUrl` | required | HTTPS URL of the GitHub repository to clone |
| `Jira.JiraProjectKey` | `DFAI` | Project used by the default JQL |
| `Jira.JiraStatusName` | `Selected for Development` | Status used by the default JQL |
| `Jira.JiraJql` | generated from project/status | Optional complete JQL override |
| `Jira.JiraPickupLabel` | `ai-agent-picked` | Label used to avoid repeat pickup |
| `Jira.JiraFailureLabel` | `ai-agent-failed` | Label added when processing fails |
| `Jira.JiraInProgressStatus` | `In Progress` | Status the issue is moved to when work starts |
| `Jira.JiraInReviewStatus` | `In Review` | Status the issue is moved to after the pull request is created |
| `Jira.JiraAssigneeAccountId` | empty | Jira account ID the issue is assigned to when work starts; empty disables assignment |
| `PollIntervalSeconds` | `30` | Poll interval (minimum 5 seconds) |
