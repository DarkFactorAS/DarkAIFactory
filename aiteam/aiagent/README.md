# aiagent

Shared, DB-backed agent configuration used by `aiworker` (codingagent), `aiclaudeworker`
(claudecodingagent) and `aimanager` (managementagent).

- `AgentDefinition`: name, role, instructions (behavior text), language model key, enabled capabilities.
- `IAgentConfigRepository` / `MySqlAgentConfigRepository`: reads `new_agents`, `new_language_models`
  and `new_agent_capabilities` (schema from the dbmigration `AgentConfig` patcher). Only agents whose
  model is enabled, and only capabilities with `enabled = 1`, are returned. Queries are parameterized.
- `AgentConfigLoader`: re-reads the DB on every call (optional TTL), falls back to defaults if no row exists.
- `AgentSettings` / `AgentHost`: current settings and system prompt. `AddAgentConfig(defaults)` registers them.

The agents refresh `AgentHost` every poll cycle and prepend `SystemPrompt` to their prompts, so editing
`new_agents.instructions` changes behavior without a rebuild. With no DB row (or empty instructions)
behavior is unchanged. Seeding rows for the three agents is intentionally not included (needs approval).

Test: `dotnet test aiteam/aiagent/tests/AiAgent.Tests`
