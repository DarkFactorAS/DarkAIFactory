---
toolRestrictions: []
maxParallelToolCalls: 20
---

# Common instructions

## Purpose

This agent-common.md file defines the agent's role, coding standards, and conventions
for the DarkFactor projects. It serves as a guide for automated agents and
contributors working in this repository.

## Goals for this app

- The instructions should be comprehensive and clear, guiding GitHub Copilot
  to best practices for all developers.
- Instructions should be adaptable to different repositories, scenarios, and
  user needs.
- You have the flexibility to adjust the content based on the repository in
  context and the user's requirements.

---

## Agent Role

- Maintain and extend the codebase, focusing on reliability,
  maintainability, and security.
- Automate repetitive tasks such as builds, tests, and deployments.
- Enforce code quality and project conventions.

## Coding Standards

- Language: C# (dotnet)
- Follow .NET best practices for structure, naming, and error handling.
- Use async/await for I/O-bound operations.
- Prefer dependency injection for service management.
- Write clear, concise, and well-documented code.

## Project Conventions

- Configuration files are in the config folder, except aidashboard, which has its own
  `aiteam/aidashboard/Config` folder.
- Controllers, Providers, and Repositories are separated by folder.
- Use tasks defined in tasks.json for build and run operations.

## Build & Run

- Use the provided build tasks (e.g., build, build_account, publish, watch)
  for compiling and running the project.
- Docker support is available via docker-up.sh and docker-down.sh scripts.

## Agent Instructions

- When modifying code, ensure all related tests pass.
- Update documentation and configuration as needed for new features or
  changes.
- After focused validation, commit all task-owned changes to the current session
  branch before the final response. Skip the commit only when the user explicitly
  asks not to commit or a genuine blocker prevents it.
- Stage only changes made for the task. Preserve and exclude pre-existing or
  unrelated changes, and never commit files under `UserSettings/`.
- Maintain compatibility with existing deployment scripts and Docker
  configurations.
- All skills are located in the folder .agents/skills
- When performing Git or GitHub operations, follow the
  `.agents/skills/git/SKILL.md` workflow for branch naming, commits, safety
  guardrails, pull requests, and pre-commit checks.


## Contact

For questions or contributions, refer to the repository maintainers or open an issue.
