using AiAgent;

var options = WorkspaceOptions.FromEnvironment(Environment.GetEnvironmentVariable);

if (args.Contains("--git-fetch"))
{
    return await GitFetcher.RunAsync(options);
}

if (args.Contains("--runner"))
{
    await RunnerServer.RunAsync(options);
    return 0;
}

Console.Error.WriteLine("Usage: AiAgent --git-fetch | --runner");
return 1;
