namespace MandoCode.Models;

/// <summary>
/// Represents a single message in the chat history.
/// Stores both the raw text and the pre-rendered ANSI output.
/// </summary>
public class ChatMsg
{
    public string Role { get; set; } = "";
    public string Text { get; set; } = "";
    public string RenderedAnsi { get; set; } = "";

    /// <summary>
    /// Operation displays that occurred during this response.
    /// </summary>
    public List<string> OperationAnsi { get; set; } = new();
}
