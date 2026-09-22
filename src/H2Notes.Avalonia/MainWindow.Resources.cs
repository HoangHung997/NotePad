using System.Diagnostics;
using Avalonia.Controls;
using H2Notes.Core;

namespace H2Notes.Avalonia;

public partial class MainWindow
{
    private H2ProjectResourceProjectionService _projectResourceProjection = null!;
    private bool _updatingProjectResources;
    private string _projectResourceSignature = "";

    private void InitializeProjectResources()
    {
        _projectResourceProjection = new H2ProjectResourceProjectionService(_app.AgentAdapter);

        ProjectResourcesList.SelectionChanged += (_, _) => UpdateProjectResourceActions();
        ProjectResourceOpenButton.Click += (_, _) => OpenSelectedProjectResource();
        ProjectResourceRevealButton.Click += (_, _) => RevealSelectedProjectResource();
    }

    private void RefreshProjectResources()
    {
        if (_projectResourceProjection is null || ProjectResourcesList is null)
            return;

        if (_notesProject is null)
        {
            SetProjectResourceItems(Array.Empty<ProjectResourceItem>());
            return;
        }

        var items = _projectResourceProjection.Build(_notesProject)
            .Where(resource => string.IsNullOrWhiteSpace(ResourceSearchBox.Text) || (resource.Label + " " + resource.Target).Contains(ResourceSearchBox.Text.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(resource => new ProjectResourceItem(resource))
            .ToArray();

        var signature = string.Join("|", items.Select(item => item.Signature));
        if (signature != _projectResourceSignature)
        {
            _projectResourceSignature = signature;
            SetProjectResourceItems(items);
        }

        ProjectResourcesSummary.Text = $"{items.Length} mục";
        ProjectResourcesEmpty.IsVisible = items.Length == 0;
        ProjectResourcesList.IsVisible = items.Length != 0;
        UpdateProjectResourceActions();
    }

    private void SetProjectResourceItems(IReadOnlyList<ProjectResourceItem> items)
    {
        _updatingProjectResources = true;
        ProjectResourcesList.ItemsSource = items;
        ProjectResourcesList.SelectedItem = null;
        _updatingProjectResources = false;
        ProjectResourcesSummary.Text = $"{items.Count} mục";
        ProjectResourcesEmpty.IsVisible = items.Count == 0;
        ProjectResourcesList.IsVisible = items.Count != 0;
        UpdateProjectResourceActions();
    }

    private ProjectResourceItem? SelectedProjectResource
        => !_updatingProjectResources
            ? ProjectResourcesList.SelectedItem as ProjectResourceItem
            : null;

    private void UpdateProjectResourceActions()
    {
        var selected = SelectedProjectResource;
        var decision = H2ResourceTargetPolicy.Evaluate(selected?.Target);
        ProjectResourceOpenButton.IsEnabled = selected is not null && decision.CanOpen;
        ProjectResourceRevealButton.IsEnabled = selected is not null && decision.CanReveal;
    }

    private void OpenSelectedProjectResource()
    {
        var decision = H2ResourceTargetPolicy.Evaluate(SelectedProjectResource?.Target);
        if (!decision.CanOpen || string.IsNullOrWhiteSpace(decision.Target))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(decision.Target)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                   or System.ComponentModel.Win32Exception
                                   or FileNotFoundException
                                   or DirectoryNotFoundException)
        {
            SetSaveStatus("Không mở được tài nguyên: " + ex.Message);
        }
    }

    private void RevealSelectedProjectResource()
    {
        var decision = H2ResourceTargetPolicy.Evaluate(SelectedProjectResource?.Target);
        if (!decision.CanReveal || string.IsNullOrWhiteSpace(decision.Target) || !OperatingSystem.IsWindows())
            return;

        try
        {
            var info = new ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = false
            };
            info.ArgumentList.Add("/select," + decision.Target);
            Process.Start(info);
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                   or System.ComponentModel.Win32Exception
                                   or FileNotFoundException
                                   or DirectoryNotFoundException)
        {
            SetSaveStatus("Không hiện được vị trí tài nguyên: " + ex.Message);
        }
    }

    private sealed class ProjectResourceItem
    {
        public ProjectResourceItem(ProjectResourceProjection projection)
            => Projection = projection;

        public ProjectResourceProjection Projection { get; }
        public string ResourceId => Projection.ResourceId;
        public string Label => Projection.Label;
        public string? Target => Projection.Target;

        public string SourceText => Projection.Source switch
        {
            H2ProjectResourceSource.ProjectLink => Projection.Kind == H2ProjectResourceKind.WebLink
                ? "Liên kết dự án"
                : "Dự án",
            H2ProjectResourceSource.SavedFile => "Tệp đã lưu",
            H2ProjectResourceSource.AgentEvidence => "Bằng chứng Agent",
            _ => "Tài nguyên"
        };

        public string TargetText => Projection.Source == H2ProjectResourceSource.AgentEvidence
            ? "Nguồn: Agent"
            : string.IsNullOrWhiteSpace(Projection.Target)
                ? "Không có target mở trực tiếp"
                : Projection.Target!;

        public string MetaText
        {
            get
            {
                var parts = new List<string>();
                if (Projection.ObservedUtc is { } observed)
                {
                    var local = observed.Kind == DateTimeKind.Utc ? observed.ToLocalTime() : observed;
                    parts.Add(local.ToString("dd/MM/yyyy HH:mm"));
                }
                if (Target is { } path && Path.IsPathFullyQualified(path) && !File.Exists(path) && !Directory.Exists(path)) parts.Add("Không tìm thấy · liên kết lại");
                return string.Join(" · ", parts);
            }
        }

        public string Signature =>
            $"{ResourceId}:{Projection.Source}:{Projection.Kind}:{Label}:{Target}:"
            + $"{Projection.Sha256}:{Projection.ObservedUtc:O}:{Projection.AgentTaskId}:{Projection.EvidenceId}";
    }
}
