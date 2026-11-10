# aiagent

Scaffold for the custom agent (DFAI-52). One image, two containers sharing the `aiagent_workspaces` volume:

- `aiagent-git-fetch` (`--git-fetch`): clones `AIAGENT_REPOSITORY_URL` (optional `AIAGENT_REPOSITORY_BRANCH`) into `/workspaces/repo`, writes a completion marker and exits.
- `aiagent-runner` (`--runner`): starts after the fetch succeeds, mounts the volume and serves `/health` and `/workspace` (whether the checkout is readable) on port 8080 inside the `aiagent` network.

Build: `dotnet build aiteam/aiagent/src/AiAgent/AiAgent.csproj`. Test: `dotnet test aiteam/aiagent/AiAgent.Tests.csproj`.
Run: `aiteam/docker-up.sh` / `aiteam/docker-down.sh` (aiagent services only; existing agents are untouched).

Without either mode flag, it runs a .NET generic host that loads
`Config/appsettings.{DOTNET_ENVIRONMENT}.json` and starts the capabilities named in `Capabilities`.
With none enabled it logs a message and exits with code 0.

- `Capabilities/` – `ICapability` module interface and `CapabilityRegistry`.
- `LanguageModels/` – `ILanguageModel` module interface and `LanguageModelRegistry`.
- Settings: `AppName`, `Capabilities` (list of names), `LanguageModel` (optional name).

Host tests: `dotnet test aiteam/aiagent/tests/AiAgent.Tests.csproj`.
Image: `docker build --secret id=nuget_username,env=NUGET_USERNAME --secret id=nuget_token,env=NUGET_TOKEN -f aiteam/aiagent/Dockerfile .`
from the repository root, with `NUGET_USERNAME` and `NUGET_TOKEN` set for the configured feed.
