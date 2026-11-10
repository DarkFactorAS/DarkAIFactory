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

FROM mcr.microsoft.com/dotnet/sdk:10.0
RUN apt-get update \
    && apt-get install --yes --no-install-recommends bash ca-certificates curl git \
    && curl -fsSL https://gh.io/copilot-install --output /tmp/copilot-install \
    && PREFIX=/usr/local bash /tmp/copilot-install \
    && rm /tmp/copilot-install \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish/ .
USER app
ENTRYPOINT ["dotnet", "/app/ReviewWorker.dll"]
