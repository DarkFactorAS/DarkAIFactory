using DFCommonLib.Config;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace CodingAgent.Tests;

[Collection("Environment")]
public sealed class AppConfigurationTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("DevelopmentDocker")]
    [InlineData("Production")]
    public void ConfigurationHelper_LoadsSelectedEnvironment(string environmentName)
    {
        var originalDirectory = Directory.GetCurrentDirectory();
        var directory = Directory.CreateTempSubdirectory("app-config-test-");
        try
        {
            Directory.CreateDirectory(Path.Combine(directory.FullName, "Config"));
            File.WriteAllText(Path.Combine(directory.FullName, "Config", $"appsettings.{environmentName}.json"),
                """
                {
                  "AppName": "ConfigValidation",
                  "EnableLogging": true,
                  "Jira": {
                    "JiraBaseUrl": "https://example.atlassian.net",
                    "JiraEmail": "bot@example.com",
                    "JiraApiToken": "test-only",
                    "JiraPickupLabel": "custom-picked"
                  },
                  "GitHub": {
                    "AgentGitHubToken": "test-only",
                    "GitHubRepositoryToken": "test-only",
                    "TargetRepositoryUrl": "https://github.com/owner/repo"
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

            Assert.Equal("ConfigValidation", helper.Settings.AppName);
            Assert.Equal("localhost", helper.Settings.DatabaseConnection.Server);
            Assert.Equal(3306, helper.Settings.DatabaseConnection.Port);
            var settings = Assert.IsType<AgentSettings>(helper.Settings);
            var configuration = AgentConfiguration.Load(settings);
            Assert.Equal("test-only", configuration.AgentGitHubToken);
            Assert.Equal("custom-picked", configuration.PickupLabel);
            Assert.Equal(TimeSpan.FromSeconds(5), configuration.PollInterval);
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
