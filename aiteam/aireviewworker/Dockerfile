FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY aiteam/aireviewworker/NuGet.config ./
COPY aiteam/aireviewworker/src/ReviewWorker/ReviewWorker.csproj src/ReviewWorker/
RUN --mount=type=secret,id=nuget_username,env=NUGET_USERNAME \
    --mount=type=secret,id=nuget_token,env=NUGET_TOKEN \
    dotnet restore src/ReviewWorker/ReviewWorker.csproj
COPY aiteam/aireviewworker/src/ReviewWorker/ src/ReviewWorker/
COPY aiteam/shared/ /shared/
RUN dotnet publish src/ReviewWorker/ReviewWorker.csproj --configuration Release --no-restore --output /app/publish

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS worker-runtime
RUN apt-get update \
    && apt-get install --yes --no-install-recommends bash ca-certificates curl git nodejs npm \
    && npm install --global --no-fund --no-audit @anthropic-ai/claude-code \
    && npm cache clean --force \
    && rm -rf /var/lib/apt/lists/*
# The Claude Code CLI login is kept in CLAUDE_CONFIG_DIR; Compose mounts the shared claude_config volume here.
ENV DISABLE_AUTOUPDATER=1 \
    CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1 \
    CLAUDE_CONFIG_DIR=/claude-config
WORKDIR /app
COPY --from=build /app/publish/ .
RUN mkdir -p /workspaces /claude-config && chown app:app /workspaces /claude-config
USER app
ENTRYPOINT ["dotnet", "/app/ReviewWorker.dll"]

FROM worker-runtime AS runner
USER root
RUN rm -rf /app/Config
USER app

FROM worker-runtime AS worker
