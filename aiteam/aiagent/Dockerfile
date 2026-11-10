FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY aiteam/aiagent/NuGet.config ./
COPY aiteam/aiagent/src/AiAgent/AiAgent.csproj src/AiAgent/
RUN --mount=type=secret,id=nuget_username,env=NUGET_USERNAME \
    --mount=type=secret,id=nuget_token,env=NUGET_TOKEN \
    dotnet restore src/AiAgent/AiAgent.csproj
COPY aiteam/aiagent/src/AiAgent/ src/AiAgent/
RUN dotnet publish src/AiAgent/AiAgent.csproj --configuration Release --no-restore --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update \
    && apt-get install --yes --no-install-recommends ca-certificates curl git \
    && rm -rf /var/lib/apt/lists/*
# Shared between the git-fetch and runner containers; Docker seeds a new volume with this ownership.
RUN mkdir -p /workspaces && chown app:app /workspaces
WORKDIR /app
COPY --from=build /app/publish/ .
USER app
ENTRYPOINT ["dotnet", "/app/AiAgent.dll"]
