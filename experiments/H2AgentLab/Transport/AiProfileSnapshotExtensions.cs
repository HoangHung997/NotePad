using H2Notes.Core;

namespace H2AgentLab.Transport;

internal static class AiProfileSnapshotExtensions
{
    /// <summary>
    /// Keep the transport call sites concise while delegating the actual snapshot contract to
    /// H2Notes.Core. This prevents Agent Lab from silently missing future AiProfile fields.
    /// </summary>
    public static AiProfile Copy(this AiProfile source) => AiProfileSnapshot.Create(source);
}
