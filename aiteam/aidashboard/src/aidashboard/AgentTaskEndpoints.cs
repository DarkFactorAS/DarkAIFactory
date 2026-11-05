using AiTeam.Configuration;
using DFCommonLib.Logger;

namespace AiDashboard.Tasks;

public sealed record AgentTaskRequest(
    string? ProjectKey, string? Title, string? Description, string? Status, string? JiraKey);

public sealed record AgentTaskEditRequest(string? Title, string? Description);

public static class AgentTaskEndpoints
{
    public const int MaxProjectKeyLength = 64;
    public const int MaxTitleLength = 255;
    public const int MaxDescriptionLength = 20000;
    public const int MaxJiraKeyLength = 255;

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/agent-tasks", List);
        app.MapGet("/api/agent-tasks/{id:long}", Get);
        app.MapPost("/api/agent-tasks", Create);
        app.MapPut("/api/agent-tasks/{id:long}", Update);
        app.MapPatch("/api/agent-tasks/{id:long}", EditNew);
    }

    public static IResult EditNew(IAgentTaskRepository repository, long id, AgentTaskEditRequest? request)
    {
        var error = ValidateEditableFields(request?.Title, request?.Description);
        if (error is not null)
        {
            return Results.BadRequest(new { error });
        }

        return Guard(() =>
        {
            if (repository.EditNew(id, request!.Title!.Trim(), request.Description!.Trim()))
            {
                return Results.Ok(new { id });
            }

            return repository.Find(id) is null
                ? Results.NotFound()
                : Results.Conflict(new { error = "The task has progressed and can no longer be edited." });
        });
    }

    public static IResult List(IAgentTaskRepository repository, string? status = null)
    {
        if (!string.IsNullOrEmpty(status) && !AgentTaskStatus.IsValid(status))
        {
            return Results.BadRequest(new { error = "status must be New, Created or Failed." });
        }

        return Guard(() => Results.Ok(repository.List(string.IsNullOrEmpty(status) ? null : status)));
    }

    public static IResult Get(IAgentTaskRepository repository, long id) =>
        Guard(() => repository.Find(id) is { } task ? Results.Ok(task) : Results.NotFound());

    public static IResult Create(IAgentTaskRepository repository, AgentTaskRequest? request)
    {
        var error = Validate(request, requireStatus: false);
        if (error is not null)
        {
            return Results.BadRequest(new { error });
        }

        return Guard(() =>
        {
            var task = repository.Create(
                request!.ProjectKey!.Trim(), request.Title!.Trim(), request.Description!.Trim());
            return Results.Created($"/api/agent-tasks/{task.Id}", task);
        });
    }

    public static IResult Update(IAgentTaskRepository repository, long id, AgentTaskRequest? request)
    {
        var error = Validate(request, requireStatus: true);
        if (error is not null)
        {
            return Results.BadRequest(new { error });
        }

        return Guard(() =>
        {
            var jiraKey = string.IsNullOrWhiteSpace(request!.JiraKey) ? null : request.JiraKey.Trim();
            return repository.Update(
                id, request.ProjectKey!.Trim(), request.Title!.Trim(), request.Description!.Trim(),
                request.Status!, jiraKey) is { } task
                ? Results.Ok(task)
                : Results.NotFound();
        });
    }

    public static string? Validate(AgentTaskRequest? request, bool requireStatus)
    {
        if (request is null)
        {
            return "A request body is required.";
        }

        if (string.IsNullOrWhiteSpace(request.ProjectKey) || request.ProjectKey.Trim().Length > MaxProjectKeyLength)
        {
            return $"projectKey is required and must be at most {MaxProjectKeyLength} characters.";
        }

        var error = ValidateEditableFields(request.Title, request.Description);
        if (error is not null)
        {
            return error;
        }

        if (requireStatus && !AgentTaskStatus.IsValid(request.Status))
        {
            return "status must be New, Created or Failed.";
        }

        if (request.JiraKey is { } jiraKey && jiraKey.Trim().Length > MaxJiraKeyLength)
        {
            return $"jiraKey must be at most {MaxJiraKeyLength} characters.";
        }

        return null;
    }

    private static string? ValidateEditableFields(string? title, string? description)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > MaxTitleLength)
        {
            return $"title is required and must be at most {MaxTitleLength} characters.";
        }

        if (string.IsNullOrWhiteSpace(description) || description.Trim().Length > MaxDescriptionLength)
        {
            return $"description is required and must be at most {MaxDescriptionLength} characters.";
        }

        return null;
    }

    private static IResult Guard(Func<IResult> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            DFLogger.LogOutput(DFLogLevel.ERROR, "AgentTasks", $"Agent task request failed ({ex.GetType().Name}).");
            return Results.Json(new { error = "Unable to process the agent task request." },
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}
