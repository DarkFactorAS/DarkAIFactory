# Review worker (aireviewworker)

Agent that solves pull-request review comments (DFAI-137). Every polling cycle it:

1. Lists pull requests in the `pull_requests` table whose `repository_url` matches
   `GitHub.TargetRepositoryUrl`, and keeps the ones still open on GitHub.
2. Collects unresolved, current inline comments by `ReviewerLogin` that are not yet handled.
3. For each pull request with comments: clones the head branch into the shared workspace,
   runs Claude Code (`claude -p`) in `review-runner`, checks that only the cited files changed, runs the optional
   build and test commands, then commits and pushes (never forced) to the head branch.
   Attempts are recorded in `review_comment_attempts`, so each comment is retried at most
   `MaxAttemptsPerPullRequest` times.

Each step is logged with the cycle number.

## Configuration

Loaded like [aiworker](../aiworker/README.md):

- `aiteam/config/appsettings.<Environment>.json` (see the `.example` files): database
  connection and `PollIntervalSeconds`. Review-specific keys:

  | Key | Default | Description |
  |-----|---------|-------------|
  | `PollIntervalSeconds` | `30` | Poll interval, minimum 5 |
  | `ReviewerLogin` | required | GitHub login whose review comments are handled |
  | `MaxAttemptsPerPullRequest` | `3` | Max fix attempts per pull request, minimum 1 |
  | `ClaudeModel` | empty | Optional Claude Code `--model` (for example `sonnet`); empty uses the CLI default |
  | `BuildCommand` | empty | Optional shell command run in `review-runner` before pushing; failure discards the fix |
  | `TestCommand` | empty | Optional shell command run after the build; failure discards the fix |

- Database `settings` table via `aiteam/shared` `IntegrationSettingsRepository`. The worker reuses
  the `aiworker` settings client (repository token, `GitHub.TargetRepositoryUrl`), so no
  schema change is needed. The `aiworker` `GitHub.AgentModel` is a Copilot model and is not used.

## Claude Code login

`review-runner` runs `claude -p` with the Claude Code CLI login stored in the shared
`claude_config` volume (the same login used by `claude-cli` for
[aiclaudeworker](../aiclaudeworker/README.md)). Log in once if you have not already:

```bash
docker compose --env-file .env -f docker-compose.yml run --rm -it --entrypoint claude claude-cli auth login
```

The agent runs with `--permission-mode bypassPermissions` inside the sandbox, with
`git push`, `git commit` and `gh` denied; the worker commits and pushes itself. No GitHub
token is passed to the runner.

## GitHub token permissions

The GitHub token needs, on the target repository: Contents: write (push fixes) and
Pull requests: read (read review comments).

## Deployment

Compose service `review-worker` (`aiteam/docker-compose.yml`); CI in
`.github/workflows/aireviewworker-ci-*.yml`; deployed after AIClaudeWorker by
`aiteam-deploy.yml`.

Compose runs Claude Code and project build/test commands in the separate `review-runner`
service. It shares only the `review_workspaces` volume with the worker and does not
mount the worker's `/app/Config` directory. The runner receives a disposable copy of
Git metadata; authoritative metadata stays in the worker container outside the checkout.

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
