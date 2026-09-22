using H2Notes.Core;

namespace H2Notes.Avalonia;

public sealed record WorkAssistantPermissionMapping(
    H2AgentPermissionScope PermissionScope,
    bool ReadOnly);

/// <summary>
/// Pure Work Assistant UI-to-Agent scope mapping. It issues only task-local H2AgentPermissionScope
/// inputs; Agent/provider permission enforcement remains authoritative.
/// </summary>
public static class WorkAssistantPermissionScopeMapper
{
    public static WorkAssistantPermissionMapping ForWorkspace(
        H2AgentPermissionMode mode, string workspaceRoot, DateTime nowUtc)
    {
        if (mode == H2AgentPermissionMode.FullAccess) return FullAccess(nowUtc);
        if (mode == H2AgentPermissionMode.UseProjectPolicy)
            throw new ArgumentException("Chọn quyền cho thư mục, không dùng chính sách dự án.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        if (!Directory.Exists(root) || root == Path.GetPathRoot(root))
            throw new ArgumentException("Chọn một thư mục hiện có, không chọn cả ổ đĩa.");
        return new(new H2AgentPermissionScope(mode, H2AgentResourceScopeKind.Workspace,
            "workspace:" + root, mode != H2AgentPermissionMode.ObserveOnly,
            mode == H2AgentPermissionMode.AskBeforeChanges, nowUtc, nowUtc.AddMinutes(30),
            documentPath: root), mode == H2AgentPermissionMode.ObserveOnly);
    }

    public static bool TryMap(
        H2AgentPermissionMode mode,
        H2ActiveWorkContext? context,
        WorkAssistantContextScope selectedContextScope,
        Guid? projectId,
        DateTime nowUtc,
        out WorkAssistantPermissionMapping? mapping,
        out string? error)
    {
        mapping = null;
        error = null;

        if (nowUtc.Kind != DateTimeKind.Utc)
        {
            error = "Permission clock must be UTC.";
            return false;
        }

        if (!Enum.IsDefined(mode))
        {
            error = "Preset quyền không hợp lệ.";
            return false;
        }

        if (mode == H2AgentPermissionMode.FullAccess)
        {
            mapping = FullAccess(nowUtc);
            return true;
        }

        if (mode == H2AgentPermissionMode.UseProjectPolicy)
        {
            if (projectId is not { } id || id == Guid.Empty)
            {
                error = "Tác vụ nhanh chưa gắn dự án nên không thể dùng chính sách dự án.";
                return false;
            }

            mapping = new(
                new H2AgentPermissionScope(
                    H2AgentPermissionMode.UseProjectPolicy,
                    H2AgentResourceScopeKind.Project,
                    "project:" + id.ToString("N"),
                    mutationAllowed: true,
                    approvalRequired: true,
                    nowUtc,
                    nowUtc.AddMinutes(15)),
                ReadOnly: false);
            return true;
        }

        var target = ResolveResource(context, selectedContextScope, allowWindow: true);

        if (mode == H2AgentPermissionMode.ObserveOnly)
        {
            mapping = new(
                BuildScope(
                    mode,
                    target,
                    context,
                    mutationAllowed: false,
                    approvalRequired: false,
                    nowUtc,
                    nowUtc.AddMinutes(30)),
                ReadOnly: true);
            return true;
        }

        if (context is null)
        {
            error = "Muốn cho phép thay đổi phải có context ứng dụng đang hoạt động.";
            return false;
        }

        if (mode == H2AgentPermissionMode.AllowScopedChanges)
        {
            target = ResolveResource(context, selectedContextScope, allowWindow: false);
            if (target.Kind is not H2AgentResourceScopeKind.Session
                and not H2AgentResourceScopeKind.Document)
            {
                error = "Preset này chỉ cho phép thay đổi đúng tài liệu/session đang được nhận diện.";
                return false;
            }

            mapping = new(
                BuildScope(
                    mode,
                    target,
                    context,
                    mutationAllowed: true,
                    approvalRequired: false,
                    nowUtc,
                    nowUtc.AddMinutes(10)),
                ReadOnly: false);
            return true;
        }

        if (mode == H2AgentPermissionMode.AskBeforeChanges)
        {
            if (target.Kind == H2AgentResourceScopeKind.None)
            {
                error = "Không có resource scope đủ rõ để xin quyền thay đổi.";
                return false;
            }

            mapping = new(
                BuildScope(
                    mode,
                    target,
                    context,
                    mutationAllowed: true,
                    approvalRequired: true,
                    nowUtc,
                    nowUtc.AddMinutes(10)),
                ReadOnly: false);
            return true;
        }

        error = "Preset quyền chưa được hỗ trợ.";
        return false;
    }

    private static WorkAssistantPermissionMapping FullAccess(DateTime nowUtc) => new(
        new H2AgentPermissionScope(H2AgentPermissionMode.FullAccess, H2AgentResourceScopeKind.Machine,
            H2AgentPermissionScope.CurrentMachineResourceKey, true, false, nowUtc, nowUtc.AddHours(1)), false);

    private static H2AgentPermissionScope BuildScope(
        H2AgentPermissionMode mode,
        ResourceTarget target,
        H2ActiveWorkContext? context,
        bool mutationAllowed,
        bool approvalRequired,
        DateTime issuedUtc,
        DateTime expiresUtc)
        => new(
            mode,
            target.Kind,
            target.ResourceKey,
            mutationAllowed,
            approvalRequired,
            issuedUtc,
            expiresUtc,
            context?.ApplicationKind ?? H2ApplicationKind.Unknown,
            windowIdentity: context?.WindowIdentity,
            documentSessionId: context?.DocumentSessionId,
            documentPath: context?.DocumentPath);

    private static ResourceTarget ResolveResource(
        H2ActiveWorkContext? context,
        WorkAssistantContextScope selectedScope,
        bool allowWindow)
    {
        if (context is null || selectedScope == WorkAssistantContextScope.None)
            return ResourceTarget.None;

        if ((selectedScope & WorkAssistantContextScope.Session) != 0
            && !string.IsNullOrWhiteSpace(context.DocumentSessionId))
        {
            var owner = !string.IsNullOrWhiteSpace(context.Provider)
                ? context.Provider!
                : context.ApplicationKind.ToString();
            return new(
                H2AgentResourceScopeKind.Session,
                "session:" + BoundKey(owner) + ":" + BoundKey(context.DocumentSessionId!));
        }

        if ((selectedScope & WorkAssistantContextScope.Document) != 0
            && !string.IsNullOrWhiteSpace(context.DocumentPath))
        {
            var path = NormalizePath(context.DocumentPath!);
            return new(
                H2AgentResourceScopeKind.Document,
                "document:" + path);
        }

        if (allowWindow
            && (selectedScope & WorkAssistantContextScope.Application) != 0
            && !string.IsNullOrWhiteSpace(context.WindowIdentity))
        {
            return new(
                H2AgentResourceScopeKind.Window,
                "window:" + BoundKey(context.WindowIdentity));
        }

        return ResourceTarget.None;
    }

    private static string NormalizePath(string path)
    {
        path = path.Trim();
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }

    private static string BoundKey(string value)
    {
        value = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= 900 ? value : value[..900];
    }

    private sealed record ResourceTarget(
        H2AgentResourceScopeKind Kind,
        string? ResourceKey)
    {
        public static ResourceTarget None { get; } =
            new(H2AgentResourceScopeKind.None, null);
    }
}
