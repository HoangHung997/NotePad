using H2AgentLab.DesktopProtocol;

namespace H2AgentLab.DesktopHost;

public interface IDesktopBackend
{
    IReadOnlyList<DesktopWindowInfo> ListWindows();
    DesktopObservation Observe(string sessionId);
    DesktopActionResult Act(DesktopActionRequest request);
}

public sealed class DesktopHostFaultException : Exception
{
    public DesktopHostFaultException(string code, string message) : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
