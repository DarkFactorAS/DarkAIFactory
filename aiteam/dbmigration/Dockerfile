FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY aiteam/dbmigration/NuGet.config ./
COPY aiteam/dbmigration/src/dbmigration/dbmigration.csproj src/dbmigration/
RUN --mount=type=secret,id=nuget_username,env=NUGET_USERNAME \
    --mount=type=secret,id=nuget_token,env=NUGET_TOKEN \
    dotnet restore src/dbmigration/dbmigration.csproj
COPY aiteam/dbmigration/src/dbmigration/ src/dbmigration/
RUN dotnet publish src/dbmigration/dbmigration.csproj --configuration Release --no-restore --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish/ .
USER app
ENTRYPOINT ["dotnet", "/app/dbmigration.dll"]
