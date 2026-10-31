# Jira Coding Agent

This Dockerized worker polls Jira for eligible tasks, clones the configured
GitHub repository into a temporary workspace, asks GitHub Copilot CLI to prepare
an implementation plan, posts the plan to Jira, then implements the task and
pushes the changes to a new GitHub branch before posting the result.

By default, it watches project `DFAI` in status `Selected for Development` and
skips epics. Set `JIRA_JQL` to use a different query.

## Run

1. Copy `aiteam/.env.example` to `aiteam/.env`.
2. Set `JIRA_BASE_URL`, `JIRA_EMAIL`, and `JIRA_API_TOKEN`.
3. Set `GH_TOKEN` to a fine-grained GitHub personal access token with only the
   **Copilot Requests** permission.
4. Set `GITHUB_REPOSITORY_TOKEN` to a separate fine-grained token with
   **Contents: read and write** permission for the target repository. The
   worker uses this token only to clone and push its branch.
5. Set `TARGET_REPOSITORY_URL` to an HTTPS GitHub repository URL, such as
   `https://github.com/owner/repository.git`.
6. From the `aiteam` directory, start the worker with:

   ```sh
   docker compose --env-file .env -f docker-compose.yml up --build -d
   ```

The Compose file and `.env` are in `aiteam/`; the Docker build context is
`aiteam/aiworker/`. For each issue, the worker clones the repository to an
isolated temporary workspace, creates a branch named `ai-agent/<issue>-<id>`,
and pushes a commit if the agent made changes. The temporary checkout is
removed afterward. The worker does not create pull requests. Copilot cannot
execute shell commands, so it cannot run tests; validate the pushed branch
separately. The worker follows repository Markdown instructions, including
applicable `AGENTS.md` and Git workflow rules.

Issues retain the `ai-agent-picked` label after processing. If processing fails,
the worker also adds `ai-agent-failed` and posts an error comment. Remove the
pickup label after resolving the failure to let the issue be retried.

## Run from Visual Studio Code

Open the repository root in VS Code, copy `aiteam/.env.example` to
`aiteam/.env`, and set the Jira and GitHub credentials and repository URL there.
Install the C# extension, then select **AI Worker (.NET)** in Run and Debug and
press F5. VS Code builds the worker first and loads `aiteam/.env`.

The **build aiworker** task is also available from **Terminal > Run Build Task**.

## Configuration

| Variable | Default | Description |
| --- | --- | --- |
| `JIRA_BASE_URL` | required | HTTPS Jira Cloud base URL |
| `JIRA_EMAIL` | required | Jira account email |
| `JIRA_API_TOKEN` | required | Jira API token |
| `GH_TOKEN` | required | Fine-grained token with Copilot Requests permission |
| `GITHUB_REPOSITORY_TOKEN` | required | Fine-grained token with Contents read/write access to the target repository |
| `TARGET_REPOSITORY_URL` | required | HTTPS URL of the GitHub repository to clone |
| `JIRA_PROJECT_KEY` | `DFAI` | Project used by the default JQL |
| `JIRA_STATUS_NAME` | `Selected for Development` | Status used by the default JQL |
| `JIRA_JQL` | generated from project/status | Optional complete JQL override |
| `JIRA_PICKUP_LABEL` | `ai-agent-picked` | Label used to avoid repeat pickup |
| `JIRA_FAILURE_LABEL` | `ai-agent-failed` | Label added when processing fails |
| `POLL_INTERVAL_SECONDS` | `30` | Poll interval (minimum 5 seconds) |
