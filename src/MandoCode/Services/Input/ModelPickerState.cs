namespace MandoCode.Services;

/// <summary>Model suggestions owned by the prompt's keyboard handler.</summary>
public sealed class ModelPickerState(IReadOnlyList<string> models)
{
    public IReadOnlyList<string> Matches { get; private set; } = models.ToArray();
    public int SelectedIndex { get; private set; }
    public string? Selected => Matches.Count == 0 ? null : Matches[SelectedIndex];
    public void Filter(string query)
    {
        Matches = models.Where(model => model.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        SelectedIndex = 0;
    }
    public void Move(int direction)
    {
        if (Matches.Count > 0) SelectedIndex = (SelectedIndex + direction + Matches.Count) % Matches.Count;
    }
}