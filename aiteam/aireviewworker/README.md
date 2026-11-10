# Review worker (aireviewworker)

Scaffold for the agent that solves pull-request review comments (DFAI-137). It starts a
.NET generic host with DI, loads configuration and runs a polling loop. Comment handling
is added in follow-up sub-tasks; the loop currently only loads and validates configuration.

## Configuration

Loaded like [aiworker](../aiworker/README.md):

- `aiteam/config/appsettings.<Environment>.json` (see the `.example` files): database
  connection and `PollIntervalSeconds`. Review-specific keys:

  | Key | Default | Description |
  |-----|---------|-------------|
  | `PollIntervalSeconds` | `30` | Poll interval, minimum 5 |
  | `ReviewerLogin` | required | GitHub login whose review comments are handled |
  | `MaxAttemptsPerPullRequest` | `3` | Max fix attempts per pull request, minimum 1 |

- Database `settings` table via `aiteam/shared` `IntegrationSettingsRepository`. The worker reuses
  the `aiworker` settings client (agent/repository tokens, `GitHub.TargetRepositoryUrl`), so no
  schema change is needed.

## Build, test, run

```bash
dotnet build aiteam/aireviewworker/src/ReviewWorker/ReviewWorker.csproj
dotnet test aiteam/aireviewworker/ReviewWorker.Tests.csproj
# Export NUGET_USERNAME and NUGET_TOKEN before building.
docker build \
  --secret id=nuget_username,env=NUGET_USERNAME \
  --secret id=nuget_token,env=NUGET_TOKEN \
  -f aiteam/aireviewworker/Dockerfile -t aireviewworker .  # from the repository root
```
