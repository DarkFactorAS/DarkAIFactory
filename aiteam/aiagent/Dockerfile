FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY aiteam/aiagent/NuGet.config ./
COPY aiteam/aiagent/src/AiAgent/AiAgent.csproj src/AiAgent/
RUN --mount=type=secret,id=nuget_username,env=NUGET_USERNAME \
    --mount=type=secret,id=nuget_token,env=NUGET_TOKEN \
    dotnet restore src/AiAgent/AiAgent.csproj
COPY aiteam/aiagent/src/AiAgent/ src/AiAgent/
COPY aiteam/aiagent/Config/ Config/
RUN dotnet publish src/AiAgent/AiAgent.csproj --configuration Release --no-restore --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish/ .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "/app/AiAgent.dll"]
