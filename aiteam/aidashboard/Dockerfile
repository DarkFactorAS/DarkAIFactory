FROM node:22-alpine AS client
WORKDIR /client
COPY aiteam/aidashboard/package.json aiteam/aidashboard/vite.config.js aiteam/aidashboard/index.html ./
COPY aiteam/aidashboard/js/ ./js/
COPY aiteam/aidashboard/css/ ./css/
RUN npm install
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src/aidashboard
COPY aiteam/aidashboard/NuGet.config ./
COPY aiteam/aidashboard/aidashboard.csproj ./
RUN --mount=type=secret,id=nuget_username,env=NUGET_USERNAME \
    --mount=type=secret,id=nuget_token,env=NUGET_TOKEN \
    dotnet restore aidashboard.csproj

COPY aiteam/aidashboard/ ./
COPY aiteam/shared/ /src/shared/
RUN dotnet publish aidashboard.csproj --configuration Release --no-restore --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish/ .
COPY --from=client /client/dist/ ./wwwroot/
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "/app/aidashboard.dll"]
