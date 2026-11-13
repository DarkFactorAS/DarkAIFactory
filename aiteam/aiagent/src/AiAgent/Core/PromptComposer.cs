using System.Text;

namespace AiAgent.Core;

public static class PromptComposer
{
    public static string Compose(AgentDefinition agent, string? task = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"You are {agent.Name}.");
        AppendSection(builder, "Role", agent.Role);
        AppendSection(builder, "Behavior", agent.Behavior);
        AppendSection(builder, "Task", task);
        return builder.ToString().TrimEnd();
    }

    private static void AppendSection(StringBuilder builder, string title, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        builder.AppendLine().AppendLine($"## {title}").AppendLine(text.Trim());
    }
}
