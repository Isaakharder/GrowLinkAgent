namespace FlowMasterAgent.Models;

public sealed class UploadRoute
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string KeySlot { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public RouteMatchConfig Match { get; set; } = new();
}
