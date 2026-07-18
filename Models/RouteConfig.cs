namespace FlowMasterAgent.Models;

public sealed class RouteConfig
{
    public string RouteId   { get; set; } = string.Empty;
    public string ServerUrl { get; set; } = string.Empty;
    public bool   Enabled   { get; set; } = true;
}
