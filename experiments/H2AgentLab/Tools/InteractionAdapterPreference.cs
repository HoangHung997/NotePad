namespace H2AgentLab.Tools;

public enum InteractionAdapterKind
{
    OfficeStructured = 0,
    DesktopAutomation = 1,
    PythonEscapeHatch = 2,
    None = 3
}

/// <summary>
/// Host-side adapter routing rule from the v2 spec:
/// structured app adapter > accessibility/UIA > screenshot/vision > Python escape hatch.
/// For Word/Excel intent, an available OfficeHost is always selected before DesktopHost.
/// </summary>
public static class InteractionAdapterPreference
{
    public static InteractionAdapterKind Choose(
        string query,
        bool officeHostAvailable,
        bool desktopHostAvailable,
        bool pythonAvailable = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        if (DocumentToolPreference.IsExplicitPythonIntent(query) && pythonAvailable)
            return InteractionAdapterKind.PythonEscapeHatch;

        if (DocumentToolPreference.IsDocumentIntent(query) && officeHostAvailable)
            return InteractionAdapterKind.OfficeStructured;

        if (desktopHostAvailable)
            return InteractionAdapterKind.DesktopAutomation;

        if (pythonAvailable)
            return InteractionAdapterKind.PythonEscapeHatch;

        return InteractionAdapterKind.None;
    }
}
