# aibuildpage

Minimal ASP.NET Core web project that serves a blank page titled "DarkFactor AI Team".

Build: `dotnet build aiteam/aibuildpage/src/aibuildpage/aibuildpage.csproj`

Run: `dotnet run --project aiteam/aibuildpage/src/aibuildpage/aibuildpage.csproj`

## Database patcher

On startup the app runs `AiDatabasePatcher` (`src/aibuildpage/Repository/AiDatabasePatcher.cs`), which
derives from `StartupDatabasePatcher` in `DarkFactor.Common.Lib`, like `BotDatabasePatcher` in the
GameServer project. `RunPatcher` currently only runs the base class. The app waits for the database
connection and exits with code 1 if patching fails.

The connection is read from the standard DarkFactor `AppSettings` configuration (`DatabaseConnection`).
`DarkFactor.Common.Lib` is published to the private `DarkFactor` NuGet feed, which must be configured
for restore to succeed.
