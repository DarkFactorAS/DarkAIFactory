#
# DFAgent

This repository contains shared agent instructions and skills. It also includes a
Dockerized Jira coding agent that can apply those repository instructions while
working in a separately mounted code repository.

## Add as a project submodule

From the project root, run:

```sh
git submodule add https://github.com/DarkFactorAS/AIAgents.git .agents
```

If the submodule is already configured, initialize or update it with:

```sh
git submodule update --init --recursive
```

## Run the Jira coding agent

The worker polls the configured Jira JQL, adds a pickup label to each eligible
non-epic issue, asks GitHub Copilot CLI for a plan, posts the plan as a Jira
comment, and then asks Copilot to implement the task in the mounted repository.
It posts the implementation result as a second comment. By default, it watches
project `DFAI` in status `Selected for Development`; set `JIRA_JQL` to replace
that query.

1. Copy `.env.example` to `.env`.
2. Set `JIRA_BASE_URL`, `JIRA_EMAIL`, and `JIRA_API_TOKEN`.
3. Set `GH_TOKEN` to a fine-grained GitHub personal access token with the
   **Copilot Requests** permission.
4. Set `TARGET_REPOSITORY_PATH` to the code repository the agent should modify.
5. Start the worker with `docker compose up --build -d`.

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

### Configuration

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
