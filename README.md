# DarkAIFactory

DarkAIFactory is DarkFactor's AI automation project. This repository currently
contains a Jira-driven coding agent and a small ASP.NET Core web application.

## Projects

- **Jira Coding Agent** — a Dockerized worker that picks up Jira tasks, uses
  GitHub Copilot CLI to implement them in a target repository, and opens pull
  requests. See [the AI worker README](aiteam/aiworker/README.md) for setup,
  configuration, and tests.
- **aibuildpage** — a minimal ASP.NET Core application.
  See [the aibuildpage README](aiteam/aibuildpage/README.md).
- **dbmigration** — a one-shot executable that creates and upgrades the shared
  database before clients start. See [the migration README](aiteam/dbmigration/README.md).

## Agent skills

Shared agent skills are provided by the `.agents/skills` Git submodule. Clone
this repository with submodules:

```sh
git clone --recurse-submodules https://github.com/DarkFactorAS/DarkAIFactory.git
```

For an existing checkout, initialize or update submodules with:

```sh
git submodule update --init --recursive
```

## CI

Pushes to `main` and pull requests run the worker and manager .NET and Docker
workflows in [.github/workflows](.github/workflows). The worker .NET workflow
also builds aibuildpage and builds/tests dbmigration. Docker CI uses the shared
`infrastructure-server-ci.yml@v1.7` workflow from `DarkFactorAS/Infrastructure`.

## Deployment

All three apps have deployment entrypoints using the same reusable
`infrastructure-server-deploy.yml` workflow as GameServer, pinned to `v1.7`:

- [AI worker](.github/workflows/agent-deploy.yml)
- [AI manager](.github/workflows/manager-deploy.yml)
- [AI build page](.github/workflows/aibuildpage-deploy.yml)

Like GameServer, merged PRs into `master` or `main` deploy staging followed by
production. Published releases also deploy staging before production, so the
production job's staging dependency is not skipped. Manual dispatch offers
`staging` (staging only) or `production` (staging, then production). Deployments
are serialized per app.

### Required GitHub setup

- A self-hosted Linux runner with Docker and access to the private reusable
  Infrastructure workflow.
- GitHub environments named `staging` and `production`, with any desired
  approval rules.
- Secrets `NUGET_USERNAME` and `NUGET_TOKEN`.
- Database settings for all three clients: variables `DATABASE_SERVER`,
  `DATABASE_PORT`, `DATABASE_NAME`, and secrets `DATABASE_USERNAME`,
  `DATABASE_PASSWORD`, available to each environment.
- Repository variables `AIBUILDPAGE_STAGING_PORT` and
  `AIBUILDPAGE_PRODUCTION_PORT`, containing distinct numeric host ports
  (1-65535). These are repository variables because reusable-workflow inputs
  cannot read the called job's environment variables.

The agents pass `external_port: 0` because they have no HTTP listener; the
Infrastructure workflow nevertheless publishes an unused ephemeral port.
All three clients require the same configured database for Jira/GitHub settings.
Run dbmigration against that database before deploying any client. Compose
handles this ordering for the agents; separate deployment workflows must run
the migration executable as a prerequisite. Clients no longer create tables.

### Deployment blockers

**These entrypoints are not yet working deployments.** They intentionally use
the existing Infrastructure workflow unchanged; compatibility must be addressed
there before running them:

- Infrastructure unconditionally substitutes database values in
  `<project_path>/Config/appsettings.<environment>.json`. This repository instead keeps ignored runtime JSON under
  [aiteam/config](aiteam/config), with `Production`, `Development`, and
  `DevelopmentDocker` filename casing. The expected staging/production files
  do not exist at the Infrastructure paths.
- Images deliberately exclude runtime configuration. Infrastructure must
  support read-only configuration mounts for database credentials rather than
  baking Jira/GitHub credentials into images. Jira/GitHub credentials live in
  the shared database, not in images.
- Infrastructure sets only `ASPNETCORE_ENVIRONMENT` to lowercase `staging` or
  `production`. Agents select configuration using `DOTNET_ENVIRONMENT`, and
  the web app selects the exact ASP.NET Core environment filename. Environment
  selection and config filenames must be aligned for all three apps.
- Infrastructure does not join the Compose database network. All three
  client containers need network access to the configured MariaDB host;
  `aiteam_database` is only resolvable on its Docker network.
- Infrastructure starts detached containers without checking application
  readiness. Successful startup must be verified before promoting to production.

The build page now has a [Dockerfile](aiteam/aibuildpage/Dockerfile) using the
same NuGet BuildKit secrets as the agents. It requires a runtime config mount
at `/app/Config` and database access, just like local deployment.
