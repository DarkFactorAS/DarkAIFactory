FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY aiteam/aibuildpage/NuGet.config ./
COPY aiteam/aibuildpage/src/aibuildpage/aibuildpage.csproj src/aibuildpage/
RUN --mount=type=secret,id=nuget_username,env=NUGET_USERNAME \
    --mount=type=secret,id=nuget_token,env=NUGET_TOKEN \
    dotnet restore src/aibuildpage/aibuildpage.csproj
COPY aiteam/aibuildpage/src/aibuildpage/ src/aibuildpage/
RUN dotnet publish src/aibuildpage/aibuildpage.csproj --configuration Release --no-restore --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish/ .
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "/app/aibuildpage.dll"]
