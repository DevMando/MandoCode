namespace MandoCode.Services;

/// <summary>
/// Shared mutable holder for the project root path.
/// Written by shell `cd` and read by diff approval, operation display, and file links.
/// </summary>
public class ProjectRootAccessor
{
    public string ProjectRoot { get; set; }

    public ProjectRootAccessor(string projectRoot)
    {
        ProjectRoot = projectRoot;
    }
}
