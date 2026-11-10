# aiagent

Scaffold for the configurable custom agent (DFAI-52). It is a .NET generic host that loads
`Config/appsettings.{DOTNET_ENVIRONMENT}.json` and starts the capabilities named in `Capabilities`.
With none enabled it logs a message and exits with code 0.

- `Capabilities/` – `ICapability` module interface and `CapabilityRegistry`.
- `LanguageModels/` – `ILanguageModel` module interface and `LanguageModelRegistry`.
- Settings: `AppName`, `Capabilities` (list of names), `LanguageModel` (optional name).
Build: `dotnet build src/AiAgent/AiAgent.csproj`; test: `dotnet test tests/AiAgent.Tests.csproj`;
image: `docker build -f aiteam/aiagent/Dockerfile .` from the repository root.
image: `docker build -f aiteam/aiagent/Dockerfile .` from the repository root.
