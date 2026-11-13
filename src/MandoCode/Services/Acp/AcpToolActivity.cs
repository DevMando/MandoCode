using Spectre.Console;
using Spectre.Console.Rendering;

namespace MandoCode.Services.Acp;

/// <summary>One stable transcript item with an immutable snapshot for concurrent render/update threads.</summary>
public sealed class AcpToolActivity(string id) : IRenderable
{
    private AcpToolSnapshot _snapshot = new(id, "Tool call", "other", "Pending", null, [], [], false);
    public AcpToolSnapshot Snapshot => Volatile.Read(ref _snapshot);
    internal void Update(AcpToolSnapshot snapshot) => Volatile.Write(ref _snapshot, snapshot);

    private IRenderable Details()
    {
        var snapshot = Snapshot;
        var rows = new List<IRenderable>
        {
            new Text("Status: " + snapshot.Status, new Style(Color.Grey)),
            new Text(snapshot.Title)
        };
        if (!string.IsNullOrWhiteSpace(snapshot.Input))
        {
            rows.Add(new Text("Input", new Style(Color.Grey)));
            rows.Add(AnsiTranscriptText.Parse(snapshot.Input));
        }
        if (snapshot.Output.Count > 0)
        {
            rows.Add(new Text("Output", new Style(Color.Grey)));
            rows.AddRange(snapshot.Output.Select(AnsiTranscriptText.Parse));
        }
        rows.AddRange(snapshot.Details);
        if (snapshot.OutputUnavailable)
            rows.Add(new Text("Terminal output unavailable in this client.", new Style(Color.Grey)));
        return new Rows(rows);
    }

    public Measurement Measure(RenderOptions options, int maxWidth) => Details().Measure(options, maxWidth);
    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth) => Details().Render(options, maxWidth);
}

public sealed record AcpToolSnapshot(string Id, string Title, string Kind, string Status, string? Input,
    IReadOnlyList<string> Output, IReadOnlyList<IRenderable> Details, bool OutputUnavailable);
