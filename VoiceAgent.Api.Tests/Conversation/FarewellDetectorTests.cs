using Microsoft.Extensions.Options;
using VoiceAgent.Api.Conversation;
using VoiceAgent.Api.Services;

namespace VoiceAgent.Api.Tests.Conversation;

public class FarewellDetectorTests
{
    private readonly FarewellDetector _detector = new(Options.Create(new AssistantOptions()));

    [Theory]
    [InlineData("Okay thanks, that's all. Bye!")]
    [InlineData("I want to quit now")]
    [InlineData("Goodbye")]
    [InlineData("bye-bye")]
    [InlineData("No, that’s all, thanks")]
    [InlineData("Please hang up")]
    [InlineData("I'm done, see you")]
    public void IsFarewell_FarewellPhrase_ReturnsTrue(string utterance)
    {
        Assert.True(_detector.IsFarewell(utterance));
    }

    [Theory]
    [InlineData("Hello, what services do you offer?")]
    [InlineData("Don't hang up yet")]
    [InlineData("I do not want to quit")]
    [InlineData("By the way, what are your opening hours?")]
    [InlineData("")]
    [InlineData("My wife said I should quit my old phone plan and move everything over to your company")]
    public void IsFarewell_NotAFarewell_ReturnsFalse(string utterance)
    {
        Assert.False(_detector.IsFarewell(utterance));
    }

    [Fact]
    public void IsFarewell_ConfiguredPhrases_ReplaceDefaults()
    {
        var detector = new FarewellDetector(Options.Create(new AssistantOptions
        {
            EndCallPhrases = ["tam biet"],
        }));

        Assert.True(detector.IsFarewell("Tam biet!"));
        Assert.False(detector.IsFarewell("bye"));
    }
}
