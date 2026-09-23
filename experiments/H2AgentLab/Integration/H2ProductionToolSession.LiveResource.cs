using H2AgentLab.Runtime;
using H2AgentLab.Tools;
using H2Notes.Core;

namespace H2AgentLab.Integration;

internal sealed partial class H2ProductionToolSession
{
    private H2AgentLiveResourceRequirement _liveRequirement;
    private H2OfficeRuntimeTools? _liveOffice;

    // Only validated user revisions from the existing goal contract call this method. It can
    // add a live requirement, never authorize a fallback or replace an already captured target.
    internal void ObserveUserSourceRevision(string sourceText)
    {
        var next = H2AgentLiveResourceRequirement.FromUserRequest(sourceText,
            _context?.ActiveWorkContext, _context?.TargetIntent);
        if (!next.Required) return;
        var current = Volatile.Read(ref _liveRequirement);
        if (current.Required)
            next = current with { ApplicationKind = current.ApplicationKind == next.ApplicationKind
                ? current.ApplicationKind : H2ApplicationKind.Unknown };
        Volatile.Write(ref _liveRequirement, next);
    }

    internal IAgentRuntimeHooks WithLiveSourceGuard(IAgentRuntimeHooks inner)
        => new LiveSourceHooks(inner, this);

    private sealed class LiveSourceHooks(IAgentRuntimeHooks inner, H2ProductionToolSession session) : IAgentRuntimeHooks
    {
        public ValueTask BeforeModelRequestAsync(AgentRuntimeModelRequestBoundary boundary, CancellationToken ct)
            => inner.BeforeModelRequestAsync(boundary, ct);
        public ValueTask AfterToolObservationAsync(AgentRuntimeObservationBoundary boundary, CancellationToken ct)
            => inner.AfterToolObservationAsync(boundary, ct);
        public ValueTask OnCheckpointAsync(AgentRuntimeCheckpointBoundary boundary, CancellationToken ct)
            => inner.OnCheckpointAsync(boundary, ct);
        public async ValueTask BeforeCompletionAsync(AgentRuntimeCompletionBoundary boundary, CancellationToken ct)
        {
            await inner.BeforeCompletionAsync(boundary, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            // Preserve existing unresolved-effect diagnostics. This guard only adds the missing
            // source-observation requirement; it does not award semantic/mutation verification.
            var requirement = Volatile.Read(ref session._liveRequirement);
            if (requirement.Required && boundary.UnresolvedCalls == 0 && !boundary.MutationAwaitingVerification
                && session._liveOffice?.HasCompletedLiveObservation(requirement.ApplicationKind) != true)
                throw new AgentVerificationRequiredException("live_resource_required: No validated observation of the required live source is available. "
                    + ToolOutcomeBridge.SafeMessage("live_resource_required"));
        }
    }
    internal string LiveResourceInstruction => !_liveRequirement.Required ? "" :
        "\nHost source requirement: LiveResource (" + _liveRequirement.ApplicationKind + "). "
        + ToolOutcomeBridge.SafeMessage("live_resource_required")
        + " Unrelated reference files remain subject to normal grounding. Native save-copy is a new output, not a replacement input.\n";

    private AgentRuntimePermissionDecision? CheckResourceSemantics(ToolDescriptor descriptor, ToolCall call, string key)
    {
        var requirement = Volatile.Read(ref _liveRequirement);
        if (!requirement.Required) return null;
        AgentRuntimePermissionDecision Deny() => AgentRuntimePermissionDecision.Deny("live_resource_required",
            ToolOutcomeBridge.SafeMessage("live_resource_required"), key);
        // An unrestricted process has no target/provenance contract. Full Access is permission,
        // not proof that a shell command refers to the bound unsaved document. Poll/cancel remain usable.
        if (call.Name is "exec_command" or "start_command_job" or "write_command_stdin" or "search_files") return Deny();
        if ((requirement.ApplicationKind is H2ApplicationKind.Browser or H2ApplicationKind.Unknown)
            && (call.Name is "web.fetch" or "web.download" or "web.extract" or "web.get_metadata" or "web.read_feed"))
            return Deny();
        if (_fileTargets is null) return null;
        var paths = new List<string>();
        if (descriptor.Namespace.Name is "files" or "office" or "autocad")
        {
            // Directory discovery remains metadata, not a substitute document read.
            if (call.Name is "list_files" or "find_files") return null;
            if (Arg(call, "path") is { } path) paths.Add(path);
            if (Arg(call, "destination") is { } destination) paths.Add(destination);
        }
        else if (call.Name == "run_python" && Arg(call, "inputs") is { } inputs)
            paths.AddRange(inputs.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()));
        else if (call.Name == "publish_artifact" && Arg(call, "destination") is { } destination)
            paths.Add(destination);
        foreach (var path in paths)
            if (H2AgentTargetScope.TryNormalize(path, out var canonical, _fileTargets.Root)
                && requirement.RejectsDiskPath(canonical)) return Deny();
        return null; // Existing target, schema, permission and effect guards still run.
    }
}
