using FlowMasterAgent.Models;

namespace FlowMasterAgent.Services;

public sealed class FileRoutingService
{
    private static readonly IReadOnlyList<UploadRoute> DefaultRoutes =
    [
        new UploadRoute
        {
            Id = "block-summary",
            Name = "Block Summary (climate)",
            KeySlot = "block-summary",
            Enabled = true,
            Match = new RouteMatchConfig
            {
                ContainsAny = ["block summary"],
            }
        },
        new UploadRoute
        {
            Id = "flowmaster-packline",
            Name = "FlowMaster",
            KeySlot = "flowmaster",
            Enabled = true,
            Match = new RouteMatchConfig
            {
                ContainsAny = ["flowmaster", "packline", "pack line"],
            }
        },
    ];

    public IReadOnlyList<UploadRoute> GetDefaultRoutes() => DefaultRoutes;

    public UploadRoute? MatchRoute(string filePath, IEnumerable<UploadRoute>? configuredRoutes = null)
    {
        var fileName = Path.GetFileName(filePath);
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        var extension = Path.GetExtension(fileName);
        var routes = configuredRoutes ?? DefaultRoutes;

        foreach (var route in routes)
        {
            if (!route.Enabled)
                continue;

            if (!MatchesExtension(route.Match.Extensions, extension))
                continue;

            if (!ContainsAny(fileName, route.Match.ContainsAny))
                continue;

            return route;
        }

        return null;
    }

    private static bool MatchesExtension(IReadOnlyCollection<string> extensions, string fileExtension)
    {
        if (extensions.Count == 0)
            return true;

        return extensions.Any(ext =>
            string.Equals(ext?.Trim(), fileExtension, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsAny(string value, IReadOnlyCollection<string> tokens)
    {
        if (tokens.Count == 0)
            return false;

        return tokens.Any(token =>
            !string.IsNullOrWhiteSpace(token) &&
            value.Contains(token, StringComparison.OrdinalIgnoreCase));
    }
}
