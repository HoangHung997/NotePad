using Avalonia.Controls;
using H2Notes.Avalonia;

internal static class H2UiTestNavigation
{
    public static void ConfigureAgentProfile(App app)
    {
        app.LocalSettings.Ai.Profiles.Clear();
        var profile = new H2Notes.Core.AiProfile { Name = "Test model", Model = "scripted-agent",
            Protocol = H2Notes.Core.AiProtocol.OpenAiChat, BaseUrl = "https://example.test/v1" };
        app.LocalSettings.Ai.Profiles.Add(profile);
        app.LocalSettings.Ai.SelectedId = profile.Id;
    }

    public static void OpenProjectWorkspace(MainWindow window, Guid projectId)
    {
        var list = window.FindControl<ListBox>("CommandCenterList")
            ?? throw new Exception("CommandCenterList not found.");

        object? target = null;
        foreach (var item in list.Items)
        {
            if (item is null) continue;
            var property = item.GetType().GetProperty("ProjectId");
            if (property?.GetValue(item) is Guid id && id == projectId)
            {
                target = item;
                break;
            }
        }

        if (target is null)
            throw new Exception("Command Center did not expose requested project: " + projectId);

        list.SelectedItem = target;
    }
}
