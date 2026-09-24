using H2AgentLab.Tools;

namespace H2AgentLab.Runtime;

/// <summary>
/// Optional task-local extension composition seam. It contributes to the existing ToolRegistry only;
/// it does not own a second runtime, planner, permission engine or durable task store.
/// </summary>
internal interface IAgentRuntimeExtensionSession : IDisposable
{
    void Populate(ToolRegistry registry);
}
