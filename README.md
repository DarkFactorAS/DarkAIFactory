# DarkAIFactory

DarkAIFactory is DarkFactor's AI automation project. This repository currently
contains a Jira-driven coding agent and a small ASP.NET Core web application.

## Projects

- **Jira Coding Agent** — a Dockerized worker that picks up Jira tasks, uses
  GitHub Copilot CLI to implement them in a target repository, and opens pull
  requests. See [the AI worker README](aiteam/aiworker/README.md) for setup,
  configuration, and tests.
- **aibuildpage** — a minimal ASP.NET Core application with a startup database
  patcher. See [the aibuildpage README](aiteam/aibuildpage/README.md).

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

Pushes to `main` and pull requests run the workflow in
[.github/workflows/ci.yml](.github/workflows/ci.yml), which builds the AI
worker using the shared `infrastructure-server-ci.yml@v1.7` workflow from
`DarkFactorAS/Infrastructure`.
