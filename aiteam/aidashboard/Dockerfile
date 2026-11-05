FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY aiteam/aidashboard/NuGet.config ./
COPY aiteam/aidashboard/src/aidashboard/aidashboard.csproj src/aidashboard/
RUN --mount=type=secret,id=nuget_username,env=NUGET_USERNAME \
    --mount=type=secret,id=nuget_token,env=NUGET_TOKEN \
    dotnet restore src/aidashboard/aidashboard.csproj
COPY aiteam/aidashboard/src/aidashboard/ src/aidashboard/
COPY aiteam/shared/ /shared/
RUN dotnet publish src/aidashboard/aidashboard.csproj --configuration Release --no-restore --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish/ .
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "/app/aidashboard.dll"]
