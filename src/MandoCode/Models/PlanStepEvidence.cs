namespace MandoCode.Models;

/// <summary>Immutable evidence from one execution attempt; durable across verification retries.</summary>
public sealed record PlanStepEvidence(
    string Instruction,
    string Response,
    string ToolEvidence,
    string? FreshnessFailure = null,
    string? ReportedFailure = null,
    IReadOnlyDictionary<string, string>? FileVersions = null,
    string[]? AcceptanceCriteria = null,
    string[]? CheckCommands = null,
    bool? ExplicitSuccess = null,
    IReadOnlyDictionary<string, int>? TestExitCodes = null);

public enum PlanVerificationStatus { Passed, Failed, Unavailable }

public sealed record PlanVerificationResult(PlanVerificationStatus Status, string Reason, bool CanGatherEvidence = false);
