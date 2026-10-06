using MandoCode.Services;
using RazorConsole.Core.Input;
using Xunit;

namespace MandoCode.Tests;

public class PromptEmojiAliasesTests
{
    [Theory]
    [InlineData(":grinning:", "😀")]
    [InlineData(":smile:", "😄")]
    [InlineData(":joy:", "😂")]
    [InlineData(":rofl:", "🤣")]
    [InlineData(":blush:", "😊")]
    [InlineData(":wink:", "😉")]
    [InlineData(":heart_eyes:", "😍")]
    [InlineData(":smiling_hearts:", "🥰")]
    [InlineData(":sunglasses:", "😎")]
    [InlineData(":coolglasses:", "😎")]
    [InlineData(":nerd:", "🤓")]
    [InlineData(":thinking:", "🤔")]
    [InlineData(":upside_down:", "🙃")]
    [InlineData(":sweat_smile:", "😅")]
    [InlineData(":grimacing:", "😬")]
    [InlineData(":sob:", "😭")]
    [InlineData(":partying:", "🥳")]
    [InlineData(":mind_blown:", "🤯")]
    [InlineData(":sleeping:", "😴")]
    [InlineData(":eye_roll:", "🙄")]
    [InlineData(":triumph:", "😤")]
    [InlineData(":scream:", "😱")]
    [InlineData(":melting:", "🫠")]
    [InlineData(":hugs:", "🤗")]
    [InlineData(":salute:", "🫡")]
    [InlineData(":thumbsup:", "👍")]
    [InlineData(":+1:", "👍")]
    [InlineData(":thumbsdown:", "👎")]
    [InlineData(":-1:", "👎")]
    [InlineData(":ok_hand:", "👌")]
    [InlineData(":pray:", "🙏")]
    [InlineData(":clap:", "👏")]
    [InlineData(":muscle:", "💪")]
    [InlineData(":handshake:", "🤝")]
    [InlineData(":victory:", "✌️")]
    [InlineData(":crossed_fingers:", "🤞")]
    [InlineData(":eyes:", "👀")]
    [InlineData(":brain:", "🧠")]
    [InlineData(":100:", "💯")]
    [InlineData(":fire:", "🔥")]
    [InlineData(":sparkles:", "✨")]
    [InlineData(":rocket:", "🚀")]
    [InlineData(":tada:", "🎉")]
    [InlineData(":party_popper:", "🎉")]
    [InlineData(":dart:", "🎯")]
    [InlineData(":bulb:", "💡")]
    [InlineData(":idea:", "💡")]
    [InlineData(":zap:", "⚡")]
    [InlineData(":star:", "⭐")]
    [InlineData(":heart:", "❤️")]
    [InlineData(":broken_heart:", "💔")]
    [InlineData(":check:", "✅")]
    [InlineData(":white_check_mark:", "✅")]
    [InlineData(":x:", "❌")]
    [InlineData(":cross:", "❌")]
    [InlineData(":warning:", "⚠️")]
    [InlineData(":question:", "❓")]
    [InlineData(":exclamation:", "❗")]
    [InlineData(":speech_balloon:", "💬")]
    [InlineData(":bug:", "🐛")]
    [InlineData(":wrench:", "🔧")]
    [InlineData(":lock:", "🔒")]
    [InlineData(":key:", "🔑")]
    [InlineData(":memo:", "📝")]
    [InlineData(":note:", "📝")]
    [InlineData(":pushpin:", "📌")]
    [InlineData(":pin:", "📌")]
    [InlineData(":folder:", "📁")]
    [InlineData(":desktop:", "🖥️")]
    [InlineData(":coffee:", "☕")]
    [InlineData(":pizza:", "🍕")]
    [InlineData(":video_game:", "🎮")]
    [InlineData(":robot:", "🤖")]
    [InlineData(":COOLGLASSES:", "😎")]
    [InlineData(":cool:", "😎")]
    [InlineData("hello :brain:", "hello 🧠")]
    [InlineData(":llama:", "🦙")]
    [InlineData(":not_an_emoji:", ":not_an_emoji:")]
    [InlineData(":cool", ":cool")]
    [InlineData("https://example.com/:cool:", "https://example.com/:cool:")]
    public void ExpandsCompletedAliases(string input, string expected)
    {
        var buffer = Buffer(input, input.Length);
        PromptEmojiAliases.Expand(buffer);
        Assert.Equal(expected, buffer.Text);
        Assert.Equal(expected.Length, buffer.Cursor);
    }

    [Fact]
    public void PreservesSuffixAndBackspaceRemovesWholeEmoji()
    {
        var buffer = Buffer("hello :cool: world", 12);
        Assert.True(PromptEmojiAliases.Expand(buffer));
        Assert.Equal("hello 😎 world", buffer.Text);
        Assert.Equal(8, buffer.Cursor);
        buffer.Backspace();
        Assert.Equal("hello  world", buffer.Text);
    }

    private static TextSelectionState Buffer(string text, int cursor)
    {
        var buffer = new TextSelectionState();
        buffer.SetText(text);
        buffer.Begin(cursor, 1, false);
        buffer.End();
        buffer.ClearSelection();
        return buffer;
    }
}
