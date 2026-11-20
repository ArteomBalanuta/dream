using Dream.Client.Input;
using Xunit;

namespace Dream.Tests.Client.Input;

// Alt+Enter is the fullscreen toggle, so chat must never treat it as Enter (open, or send the typed line).
public sealed class ChatInputAltEnterTests
{
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void EnterCountsForChatOnlyWithoutAlt(bool enterPressed, bool altDown, bool expected)
    {
        Assert.Equal(expected, ChatInput.IsChatEnter(enterPressed, altDown));
    }
}
