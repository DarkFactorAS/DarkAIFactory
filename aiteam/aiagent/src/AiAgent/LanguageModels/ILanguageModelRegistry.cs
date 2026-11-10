namespace AiAgent.LanguageModels;

public interface ILanguageModelRegistry
{
    IReadOnlyCollection<ILanguageModel> All { get; }

    ILanguageModel? Find(string name);

    /// <summary>Returns the named model; throws when it is not registered.</summary>
    ILanguageModel Get(string name);
}
