FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY aiteam/aiclaudeworker/NuGet.config ./
COPY aiteam/aiagent/src/AiAgent/ /aiagent/src/AiAgent/
COPY aiteam/aiclaudeworker/src/ClaudeCodingAgent/ClaudeCodingAgent.csproj src/ClaudeCodingAgent/
RUN --mount=type=secret,id=nuget_username,env=NUGET_USERNAME \
    --mount=type=secret,id=nuget_token,env=NUGET_TOKEN \
    dotnet restore src/ClaudeCodingAgent/ClaudeCodingAgent.csproj
COPY aiteam/aiclaudeworker/src/ClaudeCodingAgent/ src/ClaudeCodingAgent/
COPY aiteam/shared/ /shared/
RUN dotnet publish src/ClaudeCodingAgent/ClaudeCodingAgent.csproj --configuration Release --no-restore --output /app/publish

FROM mcr.microsoft.com/dotnet/sdk:10.0
# Claude Code CLI (headless `claude -p`), installed globally so the non-root app user can run it.
RUN apt-get update \
    && apt-get install --yes --no-install-recommends ca-certificates curl git nodejs npm \
    && npm install --global --no-fund --no-audit @anthropic-ai/claude-code \
    && npm cache clean --force \
    && rm -rf /var/lib/apt/lists/*
# The CLI login (claude /login) is kept in CLAUDE_CONFIG_DIR; Compose mounts a volume here.
# /workspaces is shared between the worker and the CLI runner via a named volume; Docker seeds a new
# empty volume with this directory's ownership, so it must belong to the non-root app user.
RUN mkdir -p /claude-config /workspaces && chown app:app /claude-config /workspaces
ENV DISABLE_AUTOUPDATER=1 \
    CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1 \
    CLAUDE_CONFIG_DIR=/claude-config
WORKDIR /app
COPY --from=build /app/publish/ .
USER app
ENTRYPOINT ["dotnet", "/app/ClaudeCodingAgent.dll"]
