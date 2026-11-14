using AiDashboard.Agents;
using DFCommonLib.Logger;

namespace AiDashboard.Tasks;

public sealed record AgentCapabilityRequest(string? Capability, bool Enabled);

public sealed record AgentRequest(
    string? Name, string? Role, string? Behavior, string? LlmProvider, string? LlmModel, bool? Enabled,
    List<AgentCapabilityRequest>? Capabilities, List<string>? Projects);

public sealed record AgentEnabledRequest(bool? Enabled);

public static class AgentEndpoints
{
    public const int MaxNameLength = 255;
    public const int MaxBehaviorLength = 8000;
    public const int MaxModelLength = 255;
    public static readonly string[] LlmProviders = ["Claude", "Copilot"];
    public static readonly string[] Capabilities = ["Jira", "Planning", "Builder", "Review"];

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/agents", List);
        app.MapGet("/api/agents/{id:long}", Get);
        app.MapPost("/api/agents", Create).RequireAuthorization();
        app.MapPut("/api/agents/{id:long}", Update).RequireAuthorization();
        app.MapPatch("/api/agents/{id:long}/enabled", SetEnabled).RequireAuthorization();
        app.MapDelete("/api/agents/{id:long}", Delete).RequireAuthorization();
    }

    public static IResult List(IAgentDefinitionRepository repository) =>
        Guard(() => Results.Ok(repository.List()));

    public static IResult Get(IAgentDefinitionRepository repository, long id) =>
        Guard(() => repository.Find(id) is { } agent ? Results.Ok(agent) : Results.NotFound());

    public static IResult Create(IAgentDefinitionRepository repository, AgentRequest? request) =>
        Guard(() =>
        {
            var (agent, error) = Validate(repository, request, null);
            if (agent is null)
            {
                return Results.BadRequest(new { error });
            }

            var created = repository.Create(agent);
            return Results.Created($"/api/agents/{created.Id}", created);
        });

    public static IResult Update(IAgentDefinitionRepository repository, long id, AgentRequest? request) =>
        Guard(() =>
        {
            var existing = repository.Find(id);
            if (existing is null)
            {
                return Results.NotFound();
            }

            var (agent, error) = Validate(repository, request, id);
            if (agent is null)
            {
                return Results.BadRequest(new { error });
            }

            if (request!.Enabled is null)
            {
                agent = agent with { Enabled = existing.Enabled };
            }

            return repository.Update(id, agent) is { } updated ? Results.Ok(updated) : Results.NotFound();
        });

    public static IResult SetEnabled(IAgentDefinitionRepository repository, long id, AgentEnabledRequest? request)
    {
        if (request?.Enabled is not { } enabled)
        {
            return Results.BadRequest(new { error = "enabled is required and must be true or false." });
        }

        return Guard(() => repository.SetEnabled(id, enabled) && repository.Find(id) is { } agent
            ? Results.Ok(agent)
            : Results.NotFound());
    }

    public static IResult Delete(IAgentDefinitionRepository repository, long id) =>
        Guard(() => repository.SoftDelete(id) ? Results.NoContent() : Results.NotFound());

    public static (AgentWrite? Agent, string? Error) Validate(
        IAgentDefinitionRepository repository, AgentRequest? request, long? excludeId)
    {
        if (request is null)
        {
            return (null, "A request body is required.");
        }

        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength)
        {
            return (null, $"name is required and must be at most {MaxNameLength} characters.");
        }

        var role = request.Role?.Trim();
        if (string.IsNullOrEmpty(role))
        {
            return (null, "role is required.");
        }

        var behavior = request.Behavior?.Trim();
        if (string.IsNullOrEmpty(behavior) || behavior.Length > MaxBehaviorLength)
        {
            return (null, $"behavior is required and must be at most {MaxBehaviorLength} characters.");
        }

        var provider = LlmProviders.FirstOrDefault(
            p => string.Equals(p, request.LlmProvider?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            return (null, "llmProvider must be Claude or Copilot.");
        }

        var model = request.LlmModel?.Trim();
        if (model is { Length: > MaxModelLength })
        {
            return (null, $"llmModel must be at most {MaxModelLength} characters.");
        }

        var enabledByCapability = new Dictionary<string, bool>();
        foreach (var item in request.Capabilities ?? [])
        {
            var capability = Capabilities.FirstOrDefault(
                c => string.Equals(c, item?.Capability?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (capability is null)
            {
                return (null, "capability must be one of Jira, Planning, Builder or Review.");
            }

            if (!enabledByCapability.TryAdd(capability, item!.Enabled))
            {
                return (null, $"capability {capability} is listed more than once.");
            }
        }

        var requested = new List<string>();
        foreach (var project in request.Projects ?? [])
        {
            if (string.IsNullOrWhiteSpace(project))
            {
                return (null, "project keys must not be empty.");
            }

            if (!requested.Contains(project.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                requested.Add(project.Trim());
            }
        }

        if (repository.NameExists(name, excludeId))
        {
            return (null, $"An agent named '{name}' already exists.");
        }

        var known = repository.ExistingProjectKeys(requested)
            .ToDictionary(k => k, k => k, StringComparer.OrdinalIgnoreCase);
        var unknown = requested.Where(k => !known.ContainsKey(k)).ToArray();
        if (unknown.Length > 0)
        {
            return (null, $"Unknown project key(s): {string.Join(", ", unknown)}.");
        }

        return (new AgentWrite(
            name, role, behavior, provider, string.IsNullOrEmpty(model) ? null : model,
            request.Enabled ?? false,
            Capabilities.Select(c => new AgentCapabilityView(c, enabledByCapability.GetValueOrDefault(c))).ToArray(),
            requested.Select(k => known[k]).ToArray()), null);
    }

    private static IResult Guard(Func<IResult> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            DFLogger.LogOutput(DFLogLevel.ERROR, "Agents", $"Agent request failed ({ex.GetType().Name}).");
            return Results.Json(new { error = "Unable to process the agent request." },
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}
