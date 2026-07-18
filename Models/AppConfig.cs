namespace FlowMasterAgent.Models;

public class AppConfig
{
    public string ServerUrl { get; set; } = string.Empty;
    public string WatchFolder { get; set; } = string.Empty;
    public string BlockSummaryFolder { get; set; } = string.Empty;
    public bool UseRouteKeys { get; set; }
    // Upload key is stored separately via CredentialService (DPAPI), never here.
    public List<RouteConfig> RouteConfigs { get; set; } = [];
}
