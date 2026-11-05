FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY aiteam/aijiramanager/NuGet.config ./
COPY aiteam/aijiramanager/src/JiraManagerAgent/JiraManagerAgent.csproj src/JiraManagerAgent/
RUN --mount=type=secret,id=nuget_username,env=NUGET_USERNAME \
    --mount=type=secret,id=nuget_token,env=NUGET_TOKEN \
    dotnet restore src/JiraManagerAgent/JiraManagerAgent.csproj
COPY aiteam/aijiramanager/src/JiraManagerAgent/ src/JiraManagerAgent/
COPY aiteam/shared/ /shared/
RUN dotnet publish src/JiraManagerAgent/JiraManagerAgent.csproj --configuration Release --no-restore --output /app/publish

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app/publish/ .
USER app
ENTRYPOINT ["dotnet", "/app/JiraManagerAgent.dll"]
