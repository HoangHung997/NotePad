using H2AgentLab.DesktopProtocol;

namespace H2AgentLab.Desktop;

public static class DesktopEvidenceGate
{
    public static bool IsMutationVerifiedByNewObservation(
        DesktopActionResult action,
        DesktopObservation observation)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(observation);

        if (!action.Mutated)
            return true;
        if (!action.RequiresObservation || string.IsNullOrWhiteSpace(action.MutationId))
            return false;

        return observation.ObservationSequence > action.ObservationSequence
            && string.Equals(
                observation.ObservedMutationId,
                action.MutationId,
                StringComparison.Ordinal)
            && !string.Equals(
                observation.StateId,
                action.PriorStateId,
                StringComparison.Ordinal);
    }

    public static void EnsureMutationEvidence(
        DesktopActionResult action,
        DesktopObservation observation)
    {
        if (!IsMutationVerifiedByNewObservation(action, observation))
            throw new InvalidOperationException(
                "Desktop mutation cannot be used as final evidence until a newer observation confirms that mutation.");
    }
}
