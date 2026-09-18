namespace H2Notes.Core;

/// <summary>
/// Creates a request-local copy of AI provider settings so an active request is insulated from
/// later UI/settings mutation. Keep the copy implementation in Core as AiProfile evolves so
/// consumers such as Agent Lab do not mirror the profile field list.
/// </summary>
public static class AiProfileSnapshot
{
    public static AiProfile Create(AiProfile source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Copy();
    }
}
