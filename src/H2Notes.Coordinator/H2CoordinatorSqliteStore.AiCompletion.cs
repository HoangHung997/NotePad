using System.Text.Json;
using H2Notes.Core;
using Microsoft.Data.Sqlite;

namespace H2Notes.Coordinator;

public sealed partial class H2CoordinatorSqliteStore
{
    public void CompleteProjectAi(H2ProjectAiCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion);

        lock (_gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();

            Guid workspaceId;
            H2ProjectAiQueueState state;
            H2ProjectAiLease lease;
            using (var select = connection.CreateCommand())
            {
                select.Transaction = tx;
                select.CommandText =
                    """
                    SELECT workspace_id, state, lease_json
                    FROM ai_queue
                    WHERE request_id = $request AND project_id = $project;
                    """;
                select.Parameters.AddWithValue("$request", Id(completion.RequestId));
                select.Parameters.AddWithValue("$project", Id(completion.ProjectId));
                using var reader = select.ExecuteReader();
                if (!reader.Read() || reader.IsDBNull(2))
                    throw new InvalidOperationException("AI completion has no active durable lease.");
                workspaceId = Guid.Parse(reader.GetString(0));
                state = (H2ProjectAiQueueState)reader.GetInt32(1);
                lease = Deserialize<H2ProjectAiLease>(reader.GetString(2));
            }

            if (state != H2ProjectAiQueueState.Running)
                throw new InvalidOperationException(
                    "AI completion requires a RUNNING lease after sync barrier confirmation.");
            if (lease.LeaseId != completion.LeaseId)
                throw new InvalidOperationException(
                    "AI completion lease identity does not match durable lease.");
            if (lease.ExpiresUtc <= ServerNow())
                throw new InvalidOperationException(
                    "AI completion lease expired before completion was committed.");
            if (completion.TerminalState == H2ProjectAiQueueState.Abandoned)
                throw new InvalidOperationException(
                    "Abandoned state must be resolved through interrupted-run recovery.");

            var head = ReadProjectSequence(connection, tx, workspaceId, completion.ProjectId);
            if (completion.CommittedThroughProjectSequence < lease.Barrier.RequiredProjectSequence
                || completion.CommittedThroughProjectSequence > head)
                throw new InvalidOperationException(
                    "AI completion project sequence is outside the granted barrier/head range.");

            var correlated = ReadAiCorrelatedEventsUnderLock(
                connection,
                tx,
                workspaceId,
                completion.ProjectId,
                completion.RequestId,
                completion.LeaseId);

            if (correlated.Any(item =>
                    item.ServerSequence > completion.CommittedThroughProjectSequence))
                throw new InvalidOperationException(
                    "AI completion does not cover every correlated committed project event.");

            if (completion.TerminalState == H2ProjectAiQueueState.Completed)
                ValidateSuccessfulAiCompletion(correlated, completion);

            var keepLease =
                completion.TerminalState == H2ProjectAiQueueState.NeedsUserReview;
            var changed = Execute(connection, tx,
                """
                UPDATE ai_queue
                SET state = $state,
                    lease_json = $lease,
                    completion_json = $completion
                WHERE request_id = $request
                  AND project_id = $project
                  AND state = $running;
                """,
                ("$state", (int)completion.TerminalState),
                ("$lease", keepLease ? JsonSerializer.Serialize(lease, Json) : null),
                ("$completion", JsonSerializer.Serialize(completion, Json)),
                ("$request", Id(completion.RequestId)),
                ("$project", Id(completion.ProjectId)),
                ("$running", (int)H2ProjectAiQueueState.Running));
            if (changed != 1)
                throw new InvalidOperationException("AI completion target was not updated.");

            tx.Commit();
        }
    }

    private static void ValidateSuccessfulAiCompletion(
        IReadOnlyList<H2AcceptedProjectEvent> correlated,
        H2ProjectAiCompletion completion)
    {
        if (!completion.AssistantMessageEventId.HasValue)
            throw new InvalidOperationException(
                "Completed AI run requires a committed assistant message event.");
        if (correlated.Count == 0)
            throw new InvalidOperationException(
                "Completed AI run has no correlated committed project events.");
        if (correlated.Any(item =>
                item.Disposition != H2ProjectEventDisposition.Applied))
            throw new InvalidOperationException(
                "Completed AI run cannot contain unresolved conflicting project events.");

        var assistant = correlated.SingleOrDefault(item =>
            item.Draft.EventId == completion.AssistantMessageEventId.Value)
            ?? throw new InvalidOperationException(
                "Assistant message event is not correlated with this AI lease.");

        if (assistant.Draft.Kind != H2ProjectEventKind.AppendMessage
            || assistant.Draft.Target.EntityKind != H2ProjectEntityKind.Message)
            throw new InvalidOperationException(
                "AI completion assistant event is not an appended project message.");

        var append = Deserialize<H2ProjectMessageAppend>(
            assistant.Draft.PayloadJson);
        if (append.Message.Role != "assistant")
            throw new InvalidOperationException(
                "AI completion message must have assistant role.");
    }

    private static IReadOnlyList<H2AcceptedProjectEvent>
        ReadAiCorrelatedEventsUnderLock(
            SqliteConnection connection,
            SqliteTransaction tx,
            Guid workspaceId,
            Guid projectId,
            Guid requestId,
            Guid leaseId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText =
            """
            SELECT server_sequence, draft_json, accepted_utc,
                   disposition, resulting_revision,
                   resulting_entity_revision, conflict_id
            FROM (
                SELECT workspace_id, project_id, server_sequence,
                       draft_json, accepted_utc, disposition,
                       resulting_revision, resulting_entity_revision,
                       conflict_id
                FROM project_event_archive
                UNION ALL
                SELECT workspace_id, project_id, server_sequence,
                       draft_json, accepted_utc, disposition,
                       resulting_revision, resulting_entity_revision,
                       conflict_id
                FROM project_events
            ) e
            WHERE workspace_id = $workspace AND project_id = $project
            ORDER BY server_sequence;
            """;
        command.Parameters.AddWithValue("$workspace", Id(workspaceId));
        command.Parameters.AddWithValue("$project", Id(projectId));

        var result = new List<H2AcceptedProjectEvent>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var draft = Deserialize<H2ProjectEventDraft>(reader.GetString(1));
            if (draft.AiRequestId != requestId
                || draft.AiLeaseId != leaseId)
                continue;

            result.Add(new H2AcceptedProjectEvent(
                draft,
                reader.GetInt64(0),
                ParseStamp(reader.GetString(2)),
                (H2ProjectEventDisposition)reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                reader.IsDBNull(6)
                    ? null
                    : Guid.Parse(reader.GetString(6))));
        }

        return result;
    }
}
