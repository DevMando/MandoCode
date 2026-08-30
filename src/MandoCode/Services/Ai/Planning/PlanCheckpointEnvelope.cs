using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MandoCode.Services;

/// <summary>
/// Versioned wrapper around a durable plan-state snapshot.
/// </summary>
/// <remarks>
/// <para>
/// The application-owned <see cref="PlanRunState"/> payload is never stored bare. A schema or
/// workflow-topology change can alter what its cursor and statuses mean, and the dangerous failure
/// mode is re-running a step whose <c>write_file</c> already succeeded. Every field outside
/// <see cref="Payload"/> exists to identify or validate the saved run — see
/// <see cref="FindIncompatibility"/>.
/// </para>
/// <para>
/// This lands before the first checkpoint is ever written. Adding the envelope later would leave
/// early checkpoints unversioned and indistinguishable, forcing a heuristic sniff.
/// </para>
/// </remarks>
public sealed record PlanCheckpointEnvelope
{
    /// <summary>Envelope format version. Bump when the fields below change shape.</summary>
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Graph shape that produced this checkpoint — <see cref="PlanExecutorIds.TopologyVersion"/>.</summary>
    [JsonPropertyName("topologyVersion")]
    public string TopologyVersion { get; init; } = PlanExecutorIds.TopologyVersion;

    [JsonPropertyName("planId")]
    public string PlanId { get; init; } = "";

    /// <summary>
    /// Hash of the project root, so a checkpoint is only ever offered for the project it belongs to.
    /// A mismatch means "not ours" — invisible, not an error.
    /// </summary>
    [JsonPropertyName("projectRootHash")]
    public string ProjectRootHash { get; init; } = "";

    /// <summary>
    /// Model that ran the completed steps. Resume across a model change is refused: a plan half-run
    /// by one model and half by another is not a state anyone can reason about.
    /// </summary>
    [JsonPropertyName("modelName")]
    public string ModelName { get; init; } = "";

    [JsonPropertyName("mandoCodeVersion")]
    public string MandoCodeVersion { get; init; } = "";

    [JsonPropertyName("createdUtc")]
    public DateTimeOffset CreatedUtc { get; init; }

    /// <summary>The serialized <see cref="PlanRunState"/>. Kept as JSON until compatibility passes.</summary>
    [JsonPropertyName("payload")]
    public JsonElement Payload { get; init; }

    /// <summary>
    /// Stable hash of a project root, matching <see cref="SessionResumeStore.PathFor"/>'s scheme
    /// (full path, case-normalized, SHA-256, first 12 hex chars) so the two stores agree on identity.
    /// </summary>
    public static string HashProjectRoot(string projectRoot)
    {
        var full = Path.GetFullPath(projectRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(full.ToLowerInvariant())))[..12];
    }

    /// <summary>
    /// Returns a human-readable reason this checkpoint must not be resumed, or <c>null</c> if it is
    /// safe. Refusals are deliberately loud and specific: silently declining to resume looks
    /// identical to losing the plan, and silently resuming a mismatch can redo completed writes.
    /// </summary>
    /// <param name="projectRootHash">Hash of the project root being resumed into.</param>
    /// <param name="modelName">Model configured right now.</param>
    /// <param name="expectedPlanId">Optional Desktop agent/session owner for collision isolation.</param>
    public string? FindIncompatibility(
        string projectRootHash,
        string modelName,
        string? expectedPlanId = null)
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            return $"This plan was saved by a different version of MandoCode "
                 + $"(checkpoint format {SchemaVersion}, this build reads {CurrentSchemaVersion}). "
                 + "Start the plan again.";
        }

        if (!string.Equals(TopologyVersion, PlanExecutorIds.TopologyVersion, StringComparison.Ordinal))
        {
            return "This plan was saved by an older version of MandoCode and its steps no longer "
                 + "line up with how plans run now. Start it again.";
        }

        if (!string.Equals(ProjectRootHash, projectRootHash, StringComparison.OrdinalIgnoreCase))
        {
            return "This plan belongs to a different project folder.";
        }

        if (expectedPlanId != null &&
            !string.Equals(PlanId, expectedPlanId, StringComparison.Ordinal))
        {
            return "This plan belongs to a different agent session.";
        }

        if (!string.Equals(ModelName, modelName, StringComparison.OrdinalIgnoreCase))
        {
            return $"This plan was started with '{ModelName}' but the current model is "
                 + $"'{modelName}'. Switch back to resume it, or start the plan again.";
        }

        return null;
    }
}
