# DarkAIFactory – Agents, capabilities and skills

This file explains the .NET services in `aiteam/`: what each one does, which AI
tool it runs, and how "skills" relate to them. The earlier agents (aiworker,
aiclaudeworker, aireviewworker, aimanager/AITechLead and aijiramanager) have been
removed; `aiagent` replaces them.

---

## 1. The pipeline at a glance

```
 aidashboard ──(agent_tasks: New)──► aiagent (Jira capability: Jira issue, "Selected for Development")
   ──► Planning (plan in agent_plans + label aiagent-analyzed)
   ──► Builder (branch + PR, Jira "In Review")
   ──► Review (fixes unresolved PR review comments)
```

All services share one MariaDB database. `dbmigration` creates and upgrades the
schema before any other service starts. Work is handed over through database
tables and Jira labels/statuses.

| Service | Folder | Kind | Uses an LLM? | Writes code? |
| --- | --- | --- | --- | --- |
| aiagent | `aiteam/aiagent` | Orchestrator (polling) + sandbox runner (`aiagent-runner`) | Yes – Claude Code CLI in the runner | Yes (Builder, Review) |
| aidashboard | `aiteam/aidashboard` | ASP.NET Core + React web app | No | No |
| dbmigration | `aiteam/dbmigration` | One-shot executable | No | No |
| shared | `aiteam/shared` | Source files compiled into aidashboard (settings, labels, repositories) | – | – |

See [aiteam/aiagent/README.md](aiteam/aiagent/README.md) for the capabilities
(Jira, Planning, Builder, Review), configuration and the runner sandbox.

---

## 2. Supporting services

- **aidashboard:** ASP.NET Core API with a React/Tailwind frontend.
  - Reads Jira for the active AI tasks and the solved-by-project statistics
    (AI vs. non-AI).
  - Manages `agent_tasks` (CRUD, plus a PATCH that only works while a task is
    still `New`). This is the entry point for aiagent's Jira capability.
  - Its only Jira write is `POST /api/tasks/{key}/comments`. The dashboard is
    read-only without a login: reads (GET) are public, while every write,
    including this one, requires a logged-in DarkFactor account (login via the
    account server, DFAI-214). The comment endpoint only accepts issues in the
    active AI list.
- **dbmigration:** creates and upgrades all tables (`manager_projects`,
  `settings`, `workflow_labels`, `agent_tasks`, `agent_*`, …) and seeds the
  default settings and labels. Other services do not create tables.

---

## 3. Skills

There is no skill concept in the .NET code. Behaviour is defined by C# logic,
prompts and the CLI permission flags of the runner.

Repository skills (`.agents/skills`, Git submodule) are meant for interactive
coding assistants (Claude Code, Copilot, etc.). The .NET services do not load
them directly, but the Builder clones the target repository with submodules and
its prompt tells the LLM to follow `AGENTS.md` and the Markdown workflow rules,
so skills referenced there are read as ordinary instructions.
