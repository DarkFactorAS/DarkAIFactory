namespace ReviewWorker.Tests;

public sealed class ReviewCycleRunnerTests
{
    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(2, 0, false)]
    [InlineData(0, 1, false)]
    public void IsReadyToMerge_RequiresNoOpenCommentsAndNoFailures(int open, int failed, bool expected) =>
        Assert.Equal(expected, ReviewCycleRunner.IsReadyToMerge(open, failed));
}
