using System.Text.Json.Serialization;

namespace H2Notes.Core;

[JsonConverter(typeof(JsonStringEnumConverter<H2AgentSourceRole>))]
public enum H2AgentSourceRole { Reference, ReplaceLive, Output, Live }

/// <summary>Historical host approval projection in the existing Agent progress journal.
/// Not an execution grant, verifier result, or permission to replay work after restart.</summary>
public sealed record H2AgentSourceDecision(
    Guid DecisionId, Guid ApprovalId, Guid TaskId, string GoalRevisionId,
    string AuthorityStamp, string SourceId, H2AgentSourceRole Role, string DiskPath,
    H2AgentResourceBinding? OriginalSource, bool Approved, string ReasonCode,
    DateTime DecidedUtc, DateTime ExpiresUtc);
