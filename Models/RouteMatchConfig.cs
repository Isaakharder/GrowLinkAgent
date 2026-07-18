namespace FlowMasterAgent.Models;

public sealed class RouteMatchConfig
{
    public List<string> ContainsAny { get; set; } = [];
    public List<string> Extensions { get; set; } = [];
}
