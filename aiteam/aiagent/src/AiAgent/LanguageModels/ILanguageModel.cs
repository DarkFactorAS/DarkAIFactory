namespace AiAgent.LanguageModels;

/// <summary>A pluggable language model backend.</summary>
public interface ILanguageModel
{
    /// <summary>Unique, case-insensitive name used in configuration.</summary>
    string Name { get; }

    Task<string> CompleteAsync(string prompt, CancellationToken cancellationToken);
}
