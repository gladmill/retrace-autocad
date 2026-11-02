using Retrace.Core;
using Xunit;

namespace Retrace.Tests;

public class StopReviewTests
{
    [Theory]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    public void Window_opens_only_for_the_interactive_stop_with_the_setting_on(bool scripted, bool setting, bool expected) =>
        Assert.Equal(expected, StopReview.ShouldOpenWindow(scripted, setting));

    [Fact]
    public void Checkpoint_is_found_by_its_event_type()
    {
        Assert.True(StopReview.HasCheckpoint(new[] { "{\"n\":1,\"type\":\"cmd\"}", "{\"n\":2,\"type\":\"mark\",\"note\":\"a\"}" }));
        Assert.False(StopReview.HasCheckpoint(new[] { "{\"n\":1,\"type\":\"cmd\",\"name\":\"mark\"}" }));
        Assert.False(StopReview.HasCheckpoint(Array.Empty<string>()));
    }
}
