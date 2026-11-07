FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY aiteam/aiclaudeworker/NuGet.config ./
COPY aiteam/aiclaudeworker/src/ClaudeCodingAgent/ClaudeCodingAgent.csproj src/ClaudeCodingAgent/
RUN --mount=type=secret,id=nuget_username,env=NUGET_USERNAME \
    --mount=type=secret,id=nuget_token,env=NUGET_TOKEN \
    dotnet restore src/ClaudeCodingAgent/ClaudeCodingAgent.csproj
COPY aiteam/aiclaudeworker/src/ClaudeCodingAgent/ src/ClaudeCodingAgent/
COPY aiteam/shared/ /shared/
RUN dotnet publish src/ClaudeCodingAgent/ClaudeCodingAgent.csproj --configuration Release --no-restore --output /app/publish

FROM mcr.microsoft.com/dotnet/sdk:10.0
# Claude Code CLI (headless `claude --print`), installed globally so the non-root app user can run it.
RUN apt-get update \
    && apt-get install --yes --no-install-recommends ca-certificates curl git nodejs npm \
    && npm install --global --no-fund --no-audit @anthropic-ai/claude-code \
    && npm cache clean --force \
    && rm -rf /var/lib/apt/lists/*
ENV DISABLE_AUTOUPDATER=1 \
    CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1
WORKDIR /app
COPY --from=build /app/publish/ .
USER app
ENTRYPOINT ["dotnet", "/app/ClaudeCodingAgent.dll"]
