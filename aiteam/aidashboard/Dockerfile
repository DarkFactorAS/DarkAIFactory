FROM node:22-alpine AS client
WORKDIR /client
COPY aiteam/aidashboard/package.json aiteam/aidashboard/vite.config.js aiteam/aidashboard/index.html aiteam/aidashboard/index.css aiteam/aidashboard/main.jsx aiteam/aidashboard/App.jsx ./
RUN npm install
RUN npm run build

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
COPY --from=client /client/dist/ ./wwwroot/
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "/app/aidashboard.dll"]
