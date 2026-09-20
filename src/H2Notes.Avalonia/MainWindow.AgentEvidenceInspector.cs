using System.Diagnostics;
using H2Notes.Core;

namespace H2Notes.Avalonia;

public partial class MainWindow
{
    private H2EvidenceInspectionProjectionService _projectEvidenceProjection = null!;
    private bool _updatingProjectEvidence;
    private string _projectEvidenceSignature = "";

    private void InitializeProjectEvidenceInspector()
    {
        _projectEvidenceProjection = new H2EvidenceInspectionProjectionService(_app.AgentAdapter);
        ProjectEvidenceList.SelectionChanged += (_, _) => UpdateProjectEvidenceSelection();
        ProjectEvidenceOpenButton.Click += (_, _) => OpenSelectedProjectEvidence();
        ProjectEvidenceRevealButton.Click += (_, _) => RevealSelectedProjectEvidence();
        UpdateProjectEvidenceSelection();
    }

    private void RefreshProjectEvidence()
    {
        if (_projectEvidenceProjection is null || ProjectEvidenceList is null)
            return;

        if (_notesProject is null)
        {
            SetProjectEvidenceItems(Array.Empty<ProjectEvidenceItem>());
            return;
        }

        var items = _projectEvidenceProjection.Build(_notesProject, 120)
            .Select(item => new ProjectEvidenceItem(item))
            .ToArray();

        var signature = string.Join("|", items.Select(item => item.Signature));
        if (signature != _projectEvidenceSignature)
        {
            _projectEvidenceSignature = signature;
            SetProjectEvidenceItems(items);
        }

        ProjectEvidenceSummary.Text = $"{items.Length} evidence";
        ProjectEvidenceEmpty.IsVisible = items.Length == 0;
        ProjectEvidenceList.IsVisible = items.Length != 0;
        UpdateProjectEvidenceSelection();
    }

    private void SetProjectEvidenceItems(IReadOnlyList<ProjectEvidenceItem> items)
    {
        _updatingProjectEvidence = true;
        ProjectEvidenceList.ItemsSource = items;
        ProjectEvidenceList.SelectedItem = null;
        _updatingProjectEvidence = false;
        ProjectEvidenceSummary.Text = $"{items.Count} evidence";
        ProjectEvidenceEmpty.IsVisible = items.Count == 0;
        ProjectEvidenceList.IsVisible = items.Count != 0;
        UpdateProjectEvidenceSelection();
    }

    private ProjectEvidenceItem? SelectedProjectEvidence
        => !_updatingProjectEvidence
            ? ProjectEvidenceList.SelectedItem as ProjectEvidenceItem
            : null;

    private void UpdateProjectEvidenceSelection()
    {
        var selected = SelectedProjectEvidence;
        ProjectEvidenceIdText.Text = selected is null
            ? "Chọn evidence để xem identity/provenance."
            : "Evidence ID: " + selected.EvidenceId + " · Agent " + selected.AgentTaskId.ToString("N")[..8];

        ProjectEvidenceProvenanceText.Text = selected is null
            ? ""
            : "Provenance: " + (string.IsNullOrWhiteSpace(selected.Provenance)
                ? "không được Agent cung cấp"
                : selected.Provenance);

        ProjectEvidenceTargetText.Text = selected is null
            ? ""
            : string.IsNullOrWhiteSpace(selected.Target)
                ? "Source target: không có"
                : "Source target: " + selected.Target;

        ProjectEvidenceHashText.Text = selected is null
            ? ""
            : string.IsNullOrWhiteSpace(selected.Sha256)
                ? "SHA256: không có"
                : "SHA256: " + selected.Sha256;

        var decision = H2ResourceTargetPolicy.Evaluate(selected?.Target);
        ProjectEvidenceOpenButton.IsEnabled = selected is not null && decision.CanOpen;
        ProjectEvidenceRevealButton.IsEnabled = selected is not null && decision.CanReveal;
    }

    private void OpenSelectedProjectEvidence()
    {
        var decision = H2ResourceTargetPolicy.Evaluate(SelectedProjectEvidence?.Target);
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
            SetSaveStatus("Không mở được evidence source: " + ex.Message);
        }
    }

    private void RevealSelectedProjectEvidence()
    {
        var decision = H2ResourceTargetPolicy.Evaluate(SelectedProjectEvidence?.Target);
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
            SetSaveStatus("Không hiện được evidence file: " + ex.Message);
        }
    }

    private sealed class ProjectEvidenceItem
    {
        public ProjectEvidenceItem(H2EvidenceInspectionProjection projection)
            => Projection = projection;

        public H2EvidenceInspectionProjection Projection { get; }
        public string EvidenceId => Projection.EvidenceId;
        public Guid AgentTaskId => Projection.AgentTaskId;
        public string Summary => Projection.Summary;
        public string? Sha256 => Projection.Sha256;
        public string? Provenance => Projection.Provenance;
        public string? Target => Projection.SourceUri ?? Projection.LocalPath;

        public string KindText => Projection.InspectionKind switch
        {
            H2EvidenceInspectionKind.Web => "Web",
            H2EvidenceInspectionKind.File => "File",
            _ => "Evidence"
        };

        public string MetaText
        {
            get
            {
                var local = Projection.ObservedUtc.Kind == DateTimeKind.Utc
                    ? Projection.ObservedUtc.ToLocalTime()
                    : Projection.ObservedUtc;
                var parts = new List<string>
                {
                    "ID " + Projection.EvidenceId,
                    "Agent " + Projection.AgentTaskId.ToString("N")[..8],
                    local.ToString("dd/MM/yyyy HH:mm")
                };
                if (!string.IsNullOrWhiteSpace(Projection.Provenance))
                    parts.Add(Projection.Provenance!);
                return string.Join(" · ", parts);
            }
        }

        public string Signature =>
            $"{EvidenceId}:{AgentTaskId}:{Projection.InspectionKind}:{Projection.Kind}:"
            + $"{Summary}:{Sha256}:{Projection.SourceUri}:{Projection.LocalPath}:"
            + $"{Projection.Provenance}:{Projection.ObservedUtc:O}";
    }
}
