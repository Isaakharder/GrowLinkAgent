namespace FlowMasterAgent.Services;

public enum DestinationKind { NotSet, PlantsProduction, PlantsStaging, Plants, CropLinkLegacy, Unknown, Invalid }

/// <summary>Where the Synopta tab's server URL sends climate files, in words.</summary>
public sealed record Destination(DestinationKind Kind, string Label, string? Warning = null)
{
    // Supabase project refs of Plants (public: they're in every Plants web page).
    private const string PlantsProductionHost = "qaopjmaiqezvrskpnrsx.supabase.co";
    private const string PlantsStagingHost = "rngfabuorwpthcokjncf.supabase.co";
    private const string PlantsFunctionPath = "/functions/v1/climate";

    public static Destination Describe(string? serverUrl)
    {
        var url = serverUrl?.Trim();
        if (string.IsNullOrEmpty(url))
            return new(DestinationKind.NotSet, "Not set");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return new(DestinationKind.Invalid, "Not a valid web address");

        var host = uri.Host.ToLowerInvariant();
        var path = uri.AbsolutePath.TrimEnd('/');
        var insecure = uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback ? "Use https:// — the key would otherwise travel unencrypted." : null;

        if (host.Contains("croplink"))
            return new(DestinationKind.CropLinkLegacy, "CropLink (legacy)", insecure);

        if (host.EndsWith(".supabase.co"))
        {
            var pathWarning = path.Equals(PlantsFunctionPath, StringComparison.OrdinalIgnoreCase)
                ? insecure
                : $"For Plants the URL should end with {PlantsFunctionPath} (the agent adds /api/v1/climate/imports).";
            return host switch
            {
                PlantsProductionHost => new(DestinationKind.PlantsProduction, "Plants — PRODUCTION", pathWarning),
                PlantsStagingHost => new(DestinationKind.PlantsStaging, "Plants — STAGING (test only)", pathWarning),
                _ => new(DestinationKind.Plants, $"Plants — unrecognised project ({host})", pathWarning),
            };
        }

        return new(DestinationKind.Unknown, $"Unknown destination ({host})", insecure);
    }
}
