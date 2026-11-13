using DFCommonLib.Config;

namespace ReviewWorker;

public sealed class AgentSettings : AppSettings
{
    public int PollIntervalSeconds { get; set; } = 30;
    public string ReviewerLogin { get; set; } = string.Empty;
    public int MaxAttemptsPerPullRequest { get; set; } = 3;
    /// <summary>Optional Claude Code model (for example <c>sonnet</c>); empty uses the CLI default.</summary>
    public string ClaudeModel { get; set; } = string.Empty;
    /// <summary>Optional shell command run in the sandbox after a fix, before pushing.</summary>
    public string BuildCommand { get; set; } = string.Empty;
    /// <summary>Optional shell command run in the sandbox after the build, before pushing.</summary>
    public string TestCommand { get; set; } = string.Empty;
    /// <summary>Maximum minutes one sandbox call (Claude Code run, build or test) may take.</summary>
    public int RunnerTimeoutMinutes { get; set; } = 30;
}
