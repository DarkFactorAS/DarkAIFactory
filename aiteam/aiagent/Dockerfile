# Shared base: .NET SDK, Node, Python, Claude Code CLI and Copilot CLI.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS base
RUN apt-get update \
    && apt-get install --yes --no-install-recommends bash ca-certificates curl git nodejs npm python3 python3-pip \
    && npm install --global --no-fund --no-audit @anthropic-ai/claude-code \
    && npm cache clean --force \
    && curl --fail --silent --show-error --location https://gh.io/copilot-install --output /tmp/copilot-install \
    && bash /tmp/copilot-install \
    && rm /tmp/copilot-install \
    && rm -rf /var/lib/apt/lists/*
ENV DISABLE_AUTOUPDATER=1 \
    CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1 \
    CLAUDE_CONFIG_DIR=/claude-config

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY aiteam/aiagent/NuGet.config ./
COPY aiteam/aiagent/src/AiAgent/AiAgent.csproj src/AiAgent/
RUN --mount=type=secret,id=nuget_username,env=NUGET_USERNAME \
    --mount=type=secret,id=nuget_token,env=NUGET_TOKEN \
    dotnet restore src/AiAgent/AiAgent.csproj
COPY aiteam/aiagent/src/AiAgent/ src/AiAgent/
COPY aiteam/config/ /config/
RUN dotnet publish src/AiAgent/AiAgent.csproj --configuration Release --no-restore --output /app/publish

FROM base AS agent-runtime
WORKDIR /app
COPY --from=build /app/publish/ .
RUN mkdir -p /workspaces /claude-config && chown app:app /workspaces /claude-config
USER app
ENTRYPOINT ["dotnet", "/app/AiAgent.dll"]

# Runner: executes CLIs in the shared workspace; never receives the database configuration.
FROM agent-runtime AS runner
USER root
RUN rm -rf /app/Config
USER app

# Orchestrator (default last target).
FROM agent-runtime AS orchestrator
