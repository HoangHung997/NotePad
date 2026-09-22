using System.Text.Json;
using H2AgentLab.Tools;
using H2Notes.Core;

namespace H2AgentLab.Integration;

internal static class H2AttachmentRuntimeTools
{
    public static void Register(ToolRegistry registry, H2AgentTaskContext? context)
    {
        var attachments = context?.Attachments ?? [];
        if (attachments.Count == 0) return;
        var description = "Read the full extracted text of an explicitly attached document in chunks; use the attachmentId from context. Images/PDF bytes travel with the current user message when supported.";
        registry.Register(new ToolDescriptor("read_attachment", new("attachments", "User-selected attachments only."), description,
            AgentToolRisk.Low, AgentToolAccess.ReadOnly, true, "v1",
            JsonSerializer.SerializeToElement(new { type = "function", function = new { name = "read_attachment", description,
                parameters = new { type = "object", properties = new {
                    attachment_id = new { type = "string" }, offset = new { type = "integer", minimum = 0 } },
                    required = new[] { "attachment_id", "offset" }, additionalProperties = false } } }),
            new DelegatingToolExecutor("h2-attachments", (call, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                var id = Guid.Parse(H2ProductionToolSession.Arg(call, "attachment_id") ?? "");
                var attachment = attachments.FirstOrDefault(item => item.Id == id)
                    ?? throw new InvalidOperationException("Attachment was not selected for this request.");
                var text = attachment.Text;
                var offset = call.Arguments.GetProperty("offset").GetInt32();
                if (offset < 0 || offset > text.Length) throw new ArgumentOutOfRangeException("offset");
                // Keep worst-case escaped text below the tool-output projection threshold.
                var content = text.Substring(offset, Math.Min(1_000, text.Length - offset));
                return ValueTask.FromResult(JsonSerializer.Serialize(new { attachment.Id, attachment.Name, attachment.Sha256,
                    totalCharacters = text.Length, offset, nextOffset = offset + content.Length, content,
                    nativeMediaOnly = text.Length == 0 && (attachment.IsImage || attachment.IsPdf) }));
            }), canProvideVerificationEvidence: true));
    }
}
