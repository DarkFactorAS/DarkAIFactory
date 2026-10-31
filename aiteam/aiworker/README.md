# Jira Coding Agent

This Dockerized worker polls Jira for eligible tasks, clones the configured
GitHub repository and its submodules into a private temporary workspace, asks
GitHub Copilot CLI to prepare an implementation plan, posts the plan to Jira,
then implements the task and pushes the changes to a new GitHub branch before
posting the result.

By default, it watches project `DFAI` in status `Selected for Development` and
skips epics. Set `JIRA_JQL` to use a different query.

## Run

1. Copy `aiteam/.env.example` to `aiteam/.env`.
2. Set `JIRA_BASE_URL`, `JIRA_EMAIL`, and `JIRA_API_TOKEN`.
3. Set `GH_TOKEN` to a fine-grained GitHub personal access token with only the
   **Copilot Requests** permission.
4. Set `GITHUB_REPOSITORY_TOKEN` to a separate fine-grained token with
   **Contents: read and write** and **Pull requests: read and write**
   permissions for the target repository. The worker uses this token only to
   clone, push its branch and open the pull request.
5. Set `TARGET_REPOSITORY_URL` to an HTTPS GitHub repository URL, such as
   `https://github.com/owner/repository.git`.
6. Set `MARIADB_ROOT_PASSWORD` and `MARIADB_PASSWORD`. Optionally change
   `MARIADB_DATABASE`, `MARIADB_USER` and `MARIADB_VOLUME_NAME`.
7. The MariaDB data volume is external, so Compose does not create it. Create
   it once before the first start:

   ```sh
   docker volume create aiteam_mariadb_data
   ```

   (use the value of `MARIADB_VOLUME_NAME` if you changed it). Port 3306 is not
   published; other services reach the database at host `mariadb`.
8. From the `aiteam` directory, start the worker with:

   ```sh
   docker compose --env-file .env -f docker-compose.yml up --build -d
   ```

The Compose file and `.env` are in `aiteam/`; the Docker build context is
the repository root (the Dockerfile is `aiteam/aiworker/Dockerfile`, as used by CI). For each issue, the worker clones the repository to an
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
`aiteam/.env`, and set the Jira and GitHub credentials and repository URL there.
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

| Variable | Default | Description |
| --- | --- | --- |
| `JIRA_BASE_URL` | required | HTTPS Jira Cloud base URL |
| `JIRA_EMAIL` | required | Jira account email |
| `JIRA_API_TOKEN` | required | Jira API token |
| `GH_TOKEN` | required | Fine-grained token with Copilot Requests permission |
| `GITHUB_REPOSITORY_TOKEN` | required | Fine-grained token with Contents and Pull requests read/write access to the target repository |
| `TARGET_REPOSITORY_URL` | required | HTTPS URL of the GitHub repository to clone |
| `JIRA_PROJECT_KEY` | `DFAI` | Project used by the default JQL |
| `JIRA_STATUS_NAME` | `Selected for Development` | Status used by the default JQL |
| `JIRA_JQL` | generated from project/status | Optional complete JQL override |
| `JIRA_PICKUP_LABEL` | `ai-agent-picked` | Label used to avoid repeat pickup |
| `JIRA_FAILURE_LABEL` | `ai-agent-failed` | Label added when processing fails |
| `JIRA_IN_PROGRESS_STATUS` | `In Progress` | Status the issue is moved to when work starts |
| `JIRA_IN_REVIEW_STATUS` | `In Review` | Status the issue is moved to after the pull request is created |
| `POLL_INTERVAL_SECONDS` | `30` | Poll interval (minimum 5 seconds) |
