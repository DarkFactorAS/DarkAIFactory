using DFCommonLib.Config;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace ManagerAgent.Tests;

[Collection("Environment")]
public sealed class AppConfigurationTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("DevelopmentDocker")]
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
                    "JiraApiToken": "test-only",
                    "JiraAnalyzedLabel": "custom-analyzed"
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
            var configuration = AgentConfiguration.Load(settings,
                new ManagerProject(1, "FIRST", "https://github.com/owner/first"));

            Assert.Equal("custom-analyzed", configuration.AnalyzedLabel);
            Assert.Equal(TimeSpan.FromSeconds(5), configuration.PollInterval);
            Assert.Equal("test-only", configuration.AgentGitHubToken);
            Assert.StartsWith("project = \"FIRST\"", configuration.JiraJql);
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
