FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY aiteam/aimanager/NuGet.config ./
COPY aiteam/aiagent/src/AiAgent/ /aiagent/src/AiAgent/
COPY aiteam/aimanager/src/ManagerAgent/ManagerAgent.csproj src/ManagerAgent/
RUN --mount=type=secret,id=nuget_username,env=NUGET_USERNAME \
    --mount=type=secret,id=nuget_token,env=NUGET_TOKEN \
    dotnet restore src/ManagerAgent/ManagerAgent.csproj --configfile /src/NuGet.config
COPY aiteam/aimanager/src/ManagerAgent/ src/ManagerAgent/
COPY aiteam/shared/ /shared/
RUN dotnet publish src/ManagerAgent/ManagerAgent.csproj --configuration Release --no-restore --output /app/publish

FROM mcr.microsoft.com/dotnet/sdk:10.0
RUN apt-get update \
    && apt-get install --yes --no-install-recommends ca-certificates curl git \
    && curl --fail --silent --show-error --location https://gh.io/copilot-install --output /tmp/copilot-install \
    && bash /tmp/copilot-install \
    && rm /tmp/copilot-install \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish/ .
USER app
ENTRYPOINT ["dotnet", "/app/ManagerAgent.dll"]
