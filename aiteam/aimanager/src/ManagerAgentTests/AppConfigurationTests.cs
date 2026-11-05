using DFCommonLib.Config;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace ManagerAgent.Tests;

[Collection("Environment")]
public sealed class AppConfigurationTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Acceptance")]
    [InlineData("Production")]
    public void ConfigurationHelper_LoadsTypedJsonSettings(string environmentName)
    {
        var originalDirectory = Directory.GetCurrentDirectory();
        var directory = Directory.CreateTempSubdirectory("manager-config-test-");
        try
        {
            Directory.CreateDirectory(Path.Combine(directory.FullName, "Config"));
            File.WriteAllText(Path.Combine(directory.FullName, "Config", $"appsettings.{environmentName}.json"),
                """
                {
                  "AppName": "ManagerValidation",
                  "Jira": {
                    "JiraBaseUrl": "https://example.atlassian.net",
                    "JiraEmail": "bot@example.com",
                    "JiraApiToken": "test-only"
                  },
                  "GitHub": {
                    "AgentGitHubToken": "test-only",
                    "GitHubRepositoryToken": "test-only"
                  },
                  "PollIntervalSeconds": 5,
                  "DatabaseConnection": {
                    "Server": "localhost",
                    "Port": 3306,
                    "Database": "validation",
                    "Username": "validation",
                    "Password": "",
                    "SslMode": "None"
                  }
                }
                """);
            Directory.SetCurrentDirectory(directory.FullName);
            var helper = new ConfigurationHelper<AgentSettings>(new TestEnvironment(environmentName));
            var settings = Assert.IsType<AgentSettings>(helper.Settings);
            Assert.Equal(5, settings.PollIntervalSeconds);
            Assert.Null(typeof(AgentSettings).GetProperty("Jira"));
            Assert.Null(typeof(AgentSettings).GetProperty("GitHub"));
            Assert.Equal("localhost", settings.DatabaseConnection.Server);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
            directory.Delete(recursive: true);
        }
    }

    private sealed class TestEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "ConfigValidation";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
