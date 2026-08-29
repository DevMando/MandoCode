using System.Text.Json;
using MandoCode.Models;

namespace MandoCode.Services;

/// <summary>
/// On-disk record of the plan currently running for a project, so an interrupted plan can be
/// resumed after a crash, a Ctrl+C, or a restart.
/// </summary>
/// <remarks>
/// <para>
/// One file per project root and optional owner under <c>~/.mandocode/plans/</c>, using readable
/// leaf names plus hashes so neither same-named projects nor Desktop agents sharing a project can
/// collide. Written whole-file with write-then-rename, and best-effort throughout: persistence
/// must never break a running plan.
/// </para>
/// <para>
/// Resume works by reconstructing the plan and running it again — completed and skipped steps are
/// stepped over, so only outstanding work re-runs. That is deliberately simpler than restoring a
/// framework checkpoint: it needs no byte-identical graph topology and is not coupled to the
/// workflow library's serialization format, both of which are silent-failure risks when the real
/// state being preserved is this small.
/// </para>
/// </remarks>
public static class PlanCheckpointStore
{
    /// <summary>Safety valve — a plan record is small; anything this large is corrupt.</summary>
    private const int MaxBytes = 4 * 1024 * 1024;

    private static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mandocode", "plans");

    /// <summary>Stable path for a project and optional owner — readable leaf plus identity hashes.</summary>
    public static string PathFor(string projectRoot, string? checkpointId = null)
    {
        var full = Path.GetFullPath(projectRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var hash = PlanCheckpointEnvelope.HashProjectRoot(full);
        var leaf = new string(Path.GetFileName(full).Where(char.IsLetterOrDigit).Take(24).ToArray());
        var owner = string.IsNullOrWhiteSpace(checkpointId)
            ? ""
            : "-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(checkpointId)))[..12];
        return Path.Combine(Folder, leaf.Length > 0
            ? $"{leaf}-{hash}{owner}.json"
            : $"{hash}{owner}.json");
    }

    /// <summary>
    /// Records the current state of a running plan. Overwrites any previous record for this project.
    /// </summary>
    public static void Save(
        string projectRoot,
        PlanRunState state,
        string modelName,
        string planId,
        string? checkpointId = null)
    {
        try
        {
            var envelope = new PlanCheckpointEnvelope
            {
                PlanId = planId,
                ProjectRootHash = PlanCheckpointEnvelope.HashProjectRoot(projectRoot),
                ModelName = modelName,
                MandoCodeVersion = VersionLabel.ForAssembly(typeof(PlanCheckpointStore).Assembly),
                CreatedUtc = DateTimeOffset.UtcNow,
                Payload = JsonSerializer.SerializeToElement(state),
            };

            var json = JsonSerializer.Serialize(envelope);
            if (json.Length > MaxBytes) return;

            Directory.CreateDirectory(Folder);
            var path = PathFor(projectRoot, checkpointId);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch { /* persistence must never break the plan */ }
    }

    /// <summary>
    /// Loads the recorded plan for a project, or <c>null</c> when there is none, it cannot be read,
    /// or it is not safe to resume. <paramref name="refusal"/> explains a readable-but-unusable
    /// record so the caller can say why rather than silently offering nothing.
    /// </summary>
    public static PlanRunState? Load(
        string projectRoot,
        string modelName,
        out string? refusal,
        string? checkpointId = null)
    {
        refusal = null;
        try
        {
            var path = PathFor(projectRoot, checkpointId);
            if (!File.Exists(path)) return null;

            var envelope = JsonSerializer.Deserialize<PlanCheckpointEnvelope>(File.ReadAllText(path));
            if (envelope == null) return null;

            refusal = envelope.FindIncompatibility(
                PlanCheckpointEnvelope.HashProjectRoot(projectRoot),
                modelName,
                checkpointId);
            if (refusal != null) return null;

            return envelope.Payload.Deserialize<PlanRunState>();
        }
        catch
        {
            // Truncated or corrupt: treat as absent rather than surfacing an error. A plan record is
            // a convenience, and a half-written one is indistinguishable from no record at all.
            return null;
        }
    }

    /// <summary>Removes the record — the plan finished, was cancelled, or the user discarded it.</summary>
    public static void Delete(string projectRoot, string? checkpointId = null)
    {
        try
        {
            var path = PathFor(projectRoot, checkpointId);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    /// <summary>True when a record exists for this project, without validating it.</summary>
    public static bool Exists(string projectRoot, string? checkpointId = null)
    {
        try { return File.Exists(PathFor(projectRoot, checkpointId)); }
        catch { return false; }
    }

    /// <summary>
    /// Rebuilds a runnable plan from a saved record. Completed and skipped steps keep their status,
    /// so the runner steps over them and only outstanding work executes.
    /// </summary>
    public static TaskPlan ToPlan(PlanRunState state) => new()
    {
        OriginalRequest = state.Goal,
        Status = TaskPlanStatus.Pending,
        Steps = [.. state.Steps.Select(s => new TaskStep
        {
            StepNumber = s.Number,
            Description = s.Description,
            Instruction = s.Instruction,
            // A step that was mid-flight when the process died is Pending again: it may have half
            // run, but re-running it is safer than assuming it finished.
            Status = s.Status is TaskStepStatus.Completed or TaskStepStatus.Skipped
                ? s.Status
                : TaskStepStatus.Pending,
            Result = s.Result,
            ErrorMessage = s.Error,
        })],
    };

    /// <summary>Steps still to run in a saved record — what "resume" would actually do.</summary>
    public static int OutstandingSteps(PlanRunState state) => state.Steps.Count(
        s => s.Status is not (TaskStepStatus.Completed or TaskStepStatus.Skipped));
}
