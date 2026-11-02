namespace AiTeam.Configuration;

internal static class JiraQuery
{
    public static string EscapeJql(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    public static string ScopeToProject(string jql, string projectKey)
    {
        var orderByIndex = FindOrderByIndex(jql);
        var filter = (orderByIndex < 0 ? jql : jql[..orderByIndex]).TrimEnd();
        var orderBy = orderByIndex < 0 ? string.Empty : $" {jql[orderByIndex..].TrimStart()}";
        var projectFilter = $"project = \"{EscapeJql(projectKey)}\"";
        return filter.Length == 0
            ? $"{projectFilter}{orderBy}"
            : $"{projectFilter} AND ({filter}){orderBy}";
    }

    public static string AddPickupLabelExclusion(string jql, string pickupLabel)
    {
        var orderByIndex = FindOrderByIndex(jql);
        var filter = (orderByIndex < 0 ? jql : jql[..orderByIndex]).TrimEnd();
        var orderBy = orderByIndex < 0 ? string.Empty : $" {jql[orderByIndex..].TrimStart()}";
        return $"{filter} AND (labels is EMPTY OR labels not in (\"{EscapeJql(pickupLabel)}\")){orderBy}";
    }

    public static string RequireLabel(string jql, string label)
    {
        var orderByIndex = FindOrderByIndex(jql);
        var filter = (orderByIndex < 0 ? jql : jql[..orderByIndex]).TrimEnd();
        var orderBy = orderByIndex < 0 ? string.Empty : $" {jql[orderByIndex..].TrimStart()}";
        return $"{filter} AND labels in (\"{EscapeJql(label)}\"){orderBy}";
    }

    private static int FindOrderByIndex(string jql)
    {
        char? stringQuote = null;
        for (var index = 0; index < jql.Length; index++)
        {
            if (stringQuote.HasValue)
            {
                if (jql[index] == '\\')
                {
                    index++;
                }
                else if (jql[index] == stringQuote.Value)
                {
                    stringQuote = null;
                }
                continue;
            }

            if (jql[index] is '"' or '\'')
            {
                stringQuote = jql[index];
                continue;
            }

            if (index > 0 && !char.IsWhiteSpace(jql[index - 1]))
            {
                continue;
            }

            if (string.Compare(jql, index, "ORDER", 0, 5, StringComparison.OrdinalIgnoreCase) != 0 ||
                index + 5 >= jql.Length || !char.IsWhiteSpace(jql[index + 5]))
            {
                continue;
            }

            var byIndex = index + 5;
            while (byIndex < jql.Length && char.IsWhiteSpace(jql[byIndex]))
            {
                byIndex++;
            }

            if (string.Compare(jql, byIndex, "BY", 0, 2, StringComparison.OrdinalIgnoreCase) == 0 &&
                (byIndex + 2 == jql.Length || char.IsWhiteSpace(jql[byIndex + 2])))
            {
                return index;
            }
        }
        return -1;
    }
}
