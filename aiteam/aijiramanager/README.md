# AIJiraManager (Jira Manager Agent)

AIJiraManager polls the `agent_tasks` table (created by [dbmigration](../dbmigration/README.md) and
filled by the [aidashboard](../aidashboard/README.md) task API) for rows with status `New`. For each row it:

1. creates a Jira issue in the row's `project_key` with the row's title and description,
2. transitions the issue to the shared `Jira.JiraInProgressStatus` (default `In Progress`) so the
   AITechLead agent ([aimanager](../aimanager/README.md)) picks it up,
3. stores the Jira key in `jira_key` and sets the status to `Created`.

The Jira key is saved as soon as the issue exists, so a retry after a failed transition never creates a
duplicate issue. A failing task is retried on the next poll; after 3 failed attempts in a row it is set to `Failed`
(the failure reason is written to the log). One failing task does not block the others.

## Configuration

- `DatabaseConnection` and `PollIntervalSeconds` come from the usual JSON app configuration
  (see [App configuration](../config/README.md)).
- Jira credentials come from the shared `settings` table (`client = 'shared'`):
  `Jira.JiraBaseUrl`, `Jira.JiraEmail` and `Jira.JiraApiToken` are required;
  `Jira.JiraInProgressStatus` and `Jira.JiraTaskIssueType` (default `Task`) are optional.
  Settings are read once at startup; missing required settings stop startup.

The agent does not create tables or run migrations. Run dbmigration first.

## Build and test

```bash
dotnet test aiteam/aijiramanager/src/JiraManagerAgentTests/JiraManagerAgent.Tests.csproj
docker compose -f aiteam/docker-compose.yml up -d --build jira-manager-agent
```

Unit tests use a mocked Jira client and an in-memory task repository.
