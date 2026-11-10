namespace AiAgent.LanguageModels;

public sealed class LanguageModelRegistry : ILanguageModelRegistry
{
    private readonly Dictionary<string, ILanguageModel> _models = new(StringComparer.OrdinalIgnoreCase);

    public LanguageModelRegistry(IEnumerable<ILanguageModel> models)
    {
        foreach (var model in models)
        {
            if (!_models.TryAdd(model.Name, model))
            {
                throw new InvalidOperationException($"Duplicate language model '{model.Name}'.");
            }
        }
    }

    public IReadOnlyCollection<ILanguageModel> All => _models.Values;

    public ILanguageModel? Find(string name) => _models.GetValueOrDefault(name);

    public ILanguageModel Get(string name) =>
        Find(name) ?? throw new InvalidOperationException($"Unknown language model '{name}'.");
}
