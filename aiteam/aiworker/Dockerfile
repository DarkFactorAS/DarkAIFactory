FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY aiteam/aiworker/src/CodingAgent/CodingAgent.csproj src/CodingAgent/
RUN dotnet restore src/CodingAgent/CodingAgent.csproj
COPY aiteam/aiworker/src/CodingAgent/ src/CodingAgent/
RUN dotnet publish src/CodingAgent/CodingAgent.csproj --configuration Release --no-restore --output /app/publish

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
ENTRYPOINT ["dotnet", "/app/CodingAgent.dll"]
