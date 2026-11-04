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

    [Theory]
    [InlineData("""{"decision":"small",""")]
    [InlineData("""{"decision":"unknown"}""")]
    [InlineData("""{"decision":"clarify"}""")]
    [InlineData("""{"decision":"small"}""")]
    [InlineData("""{"decision":"split","subtasks":[]}""")]
    public void Parse_RejectsMalformedOrIncompleteResults(string json)
    {
        Assert.Throws<InvalidDataException>(() => TriageResult.Parse(json));
    }

    [Fact]
    public void ExecutionOrder_PutsDependenciesFirst()
    {
        var result = TriageResult.Parse("""
            {"decision":"split","subtasks":[
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
