namespace ManagerAgent.Tests;

public sealed class TriageResultTests
{
    [Theory]
    [InlineData("""{"decision":"clarify","questions":["Why?"]}""", "Clarify")]
    [InlineData("""{"decision":"analysis_only","analysis":"Done"}""", "AnalysisOnly")]
    [InlineData("""{"decision":"small","plan":"Fix"}""", "Small")]
    [InlineData("""{"decision":"needs_approval","plan":"Fix","riskAreas":["database"]}""", "NeedsApproval")]
    public void Parse_ReadsEveryDecision(string json, string expected)
    {
        Assert.Equal(expected, TriageResult.Parse(json).Decision.ToString());
    }

    [Fact]
    public void TryParse_AcceptsFencedJsonAndIgnoresPlainText()
    {
        Assert.True(TriageResult.TryParse("```json\n{\"decision\":\"small\",\"plan\":\"Fix\"}\n```", out var result));
        Assert.Equal("Fix", result!.Plan);
        Assert.False(TriageResult.TryParse("1. Edit the file", out _));
    }

    [Fact]
    public void TryParse_RejectsArrayShapedJson()
    {
        Assert.Throws<InvalidDataException>(() =>
            TriageResult.TryParse("""[{"decision":"clarify","questions":["Which API?"]}]""", out _));
    }

    [Theory]
    [InlineData("""{"decision":"small",""")]
    [InlineData("""{"decision":"unknown"}""")]
    [InlineData("""{"decision":"clarify"}""")]
    [InlineData("""{"decision":"small"}""")]
    [InlineData("""{"decision":"split","plan":"","subtasks":[{"title":"A","description":"a"}]}""")]
    [InlineData("""{"decision":"split","plan":"  ","subtasks":[{"title":"A","description":"a"}]}""")]
    [InlineData("""{"decision":"split","subtasks":[]}""")]
    public void Parse_RejectsMalformedOrIncompleteResults(string json)
    {
        Assert.Throws<InvalidDataException>(() => TriageResult.Parse(json));
    }

    [Theory]
    [InlineData("""{"decision":"create_tasks"}""")]
    [InlineData("""{"decision":"create_tasks","tasks":[]}""")]
    [InlineData("""{"decision":"create_tasks","tasks":[{"title":"A","description":""}]}""")]
    [InlineData("""{"decision":"create_tasks","tasks":[{"title":"A","description":"a","dependsOn":["B"]}]}""")]
    [InlineData("""{"decision":"create_tasks","tasks":[{"title":"A","description":"a"},{"title":"a","description":"b"}]}""")]
    [InlineData("""{"decision":"create_tasks","tasks":[{"title":"A","description":"a","dependsOn":["B"]},{"title":"B","description":"b","dependsOn":["A"]}]}""")]
    public void Parse_RejectsInvalidCreateTasks(string json)
    {
        Assert.Throws<InvalidDataException>(() => TriageResult.Parse(json));
    }

    [Fact]
    public void Parse_ReadsCreateTasksInDependencyOrderAndLimitsCount()
    {
        var result = TriageResult.Parse("""
            {"decision":"create_tasks","tasks":[
              {"title":"B","description":"b","dependsOn":["A"]},
              {"title":"A","description":"a"}]}
            """);

        Assert.Equal(TriageDecision.CreateTasks, result.Decision);
        Assert.Empty(result.Subtasks);
        Assert.Equal([1, 0], result.TaskExecutionOrder());
        Assert.Equal("[triage:create_tasks]", TriageResult.MarkerFor(TriageDecision.CreateTasks));

        var tooMany = string.Join(",", Enumerable.Range(1, TriageResult.MaximumTasks + 1)
            .Select(index => $$"""{"title":"T{{index}}","description":"d"}"""));
        Assert.Throws<InvalidDataException>(() =>
            TriageResult.Parse($$"""{"decision":"create_tasks","tasks":[{{tooMany}}]}"""));
    }

    [Fact]
    public void ExecutionOrder_PutsDependenciesFirst()
    {
        var result = TriageResult.Parse("""
            {"decision":"split","plan":"Complete the work.","subtasks":[
              {"title":"C","description":"c","dependsOn":["B"]},
              {"title":"B","description":"b","dependsOn":["A"]},
              {"title":"A","description":"a","dependsOn":[]}]}
            """);

        Assert.Equal([2, 1, 0], result.ExecutionOrder());
    }

    [Fact]
    public void Parse_RejectsCyclesUnknownAndDuplicateSubtasks()
    {
        Assert.Throws<InvalidDataException>(() => TriageResult.Parse("""
            {"decision":"split","subtasks":[
              {"title":"A","description":"a","dependsOn":["B"]},
              {"title":"B","description":"b","dependsOn":["A"]}]}
            """));
        Assert.Throws<InvalidDataException>(() => TriageResult.Parse("""
            {"decision":"split","subtasks":[{"title":"A","description":"a","dependsOn":["Z"]}]}
            """));
        Assert.Throws<InvalidDataException>(() => TriageResult.Parse("""
            {"decision":"split","subtasks":[
              {"title":"A","description":"a"},{"title":"a","description":"b"}]}
            """));
    }

    [Fact]
    public void Marker_RoundTrips()
    {
        var stored = $"{TriageResult.MarkerFor(TriageDecision.Split)}\n{{}}";

        Assert.Equal(TriageDecision.Split, TriageResult.ReadMarker(stored));
        Assert.Equal("{}", TriageResult.StripMarker(stored));
        Assert.Null(TriageResult.ReadMarker("Plain plan"));
    }
}
