using System.Text;
using H2AgentLab.DesktopProtocol;
using H2AgentLab.Transport;
using H2Notes.Core;

namespace H2AgentLab.Desktop;

public sealed record DesktopModelObservation(
    string Text,
    IReadOnlyList<AiImage> Images,
    bool PixelsIncluded);

public static class DesktopVisionInputAdapter
{
    public static DesktopModelObservation Prepare(
        DesktopObservation observation,
        AgentTransportCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(capabilities);

        var text = BuildUiaSummary(observation);
        if (!capabilities.NativeImageInput)
            return new DesktopModelObservation(text, Array.Empty<AiImage>(), false);

        if (observation.ScreenshotPng.Length == 0
            || observation.ScreenshotPng.Length > DesktopProtocolConstants.MaxScreenshotBytes
            || !observation.ScreenshotPng.AsSpan().StartsWith(
                new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new InvalidDataException("Desktop observation screenshot is not a valid bounded PNG.");

        return new DesktopModelObservation(
            text,
            [new AiImage("image/png", observation.ScreenshotPng.ToArray())],
            true);
    }

    private static string BuildUiaSummary(DesktopObservation observation)
    {
        var builder = new StringBuilder();
        builder.Append("Window ")
            .Append(observation.Window.ProcessName)
            .Append(" · ")
            .AppendLine(observation.Window.Title);
        builder.Append("bounds=")
            .Append(observation.Window.Bounds.X).Append(',')
            .Append(observation.Window.Bounds.Y).Append(',')
            .Append(observation.Window.Bounds.Width).Append('x')
            .Append(observation.Window.Bounds.Height)
            .Append(" dpi=").Append(observation.Window.Dpi)
            .Append(" foreground=").AppendLine(observation.Window.Foreground.ToString());
        builder.Append("state_id=").AppendLine(observation.StateId);

        foreach (var element in observation.Elements.Take(DesktopProtocolConstants.MaxElements))
        {
            builder.Append("[")
                .Append(element.Token)
                .Append("] ")
                .Append(element.ControlType)
                .Append(" name=")
                .Append(System.Text.Json.JsonSerializer.Serialize(element.Name));
            if (!string.IsNullOrEmpty(element.Value))
                builder.Append(" value=")
                    .Append(System.Text.Json.JsonSerializer.Serialize(element.Value));
            builder.AppendLine();
        }

        return builder.ToString();
    }
}
