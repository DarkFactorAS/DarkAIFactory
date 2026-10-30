# Jira Coding Agent

This Dockerized worker polls Jira for eligible tasks, asks GitHub Copilot CLI to
prepare an implementation plan, posts the plan to Jira, then implements the task
in a separately mounted code repository and posts the result.

By default, it watches project `DFAI` in status `Selected for Development` and
skips epics. Set `JIRA_JQL` to use a different query.

## Run

1. Copy `aiteam/.env.example` to `aiteam/.env`.
2. Set `JIRA_BASE_URL`, `JIRA_EMAIL`, and `JIRA_API_TOKEN`.
3. Set `GH_TOKEN` to a fine-grained GitHub personal access token with the
   **Copilot Requests** permission.
4. Set `TARGET_REPOSITORY_PATH` to the absolute host path of the code repository
   the agent should modify.
5. From the `aiteam` directory, start the worker with:

   ```sh
   docker compose --env-file .env -f docker-compose.yml up --build -d
   ```

The Compose file and `.env` are in `aiteam/`; the Docker build context is
`aiteam/aiworker/`.

The target repository is mounted read/write. Copilot can run commands and modify
files inside the container, and its non-interactive run automatically approves
tools except for the denied push/PR commands. Use a dedicated repository
checkout and a Jira service account with only the required project permissions.
The Copilot token should have only the **Copilot Requests** permission. The
worker does not push changes or create pull requests. It follows repository
Markdown instructions, including applicable `AGENTS.md` and Git workflow rules.

Issues retain the `ai-agent-picked` label after processing. If processing fails,
the worker also adds `ai-agent-failed` and posts an error comment. Remove the
pickup label after resolving the failure to let the issue be retried.

## Run from Visual Studio Code

Open the repository root in VS Code, copy `aiteam/.env.example` to
`aiteam/.env`, and set the Jira and GitHub credentials there. Install the C#
extension, then select **AI Worker (.NET)** in Run and Debug and press F5. VS
Code builds the worker first, loads `aiteam/.env`, and prompts for the absolute
path to the target code repository. The default target is the current workspace.

The **build aiworker** task is also available from **Terminal > Run Build Task**.

## Configuration

| Variable | Default | Description |
| --- | --- | --- |
| `JIRA_BASE_URL` | required | HTTPS Jira Cloud base URL |
| `JIRA_EMAIL` | required | Jira account email |
| `JIRA_API_TOKEN` | required | Jira API token |
| `GH_TOKEN` | required | GitHub token with Copilot Requests permission |
| `TARGET_REPOSITORY_PATH` | required | Host path to the code repository |
| `JIRA_PROJECT_KEY` | `DFAI` | Project used by the default JQL |
| `JIRA_STATUS_NAME` | `Selected for Development` | Status used by the default JQL |
| `JIRA_JQL` | generated from project/status | Optional complete JQL override |
| `JIRA_PICKUP_LABEL` | `ai-agent-picked` | Label used to avoid repeat pickup |
| `JIRA_FAILURE_LABEL` | `ai-agent-failed` | Label added when processing fails |
| `POLL_INTERVAL_SECONDS` | `30` | Poll interval (minimum 5 seconds) |
