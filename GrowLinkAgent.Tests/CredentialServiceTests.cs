using System.Text;
using FlowMasterAgent.Services;

namespace GrowLinkAgent.Tests;

/// <summary>Windows DPAPI key storage (Windows only), in a temporary folder — never the real %APPDATA% files.</summary>
public class CredentialServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "growlink-cred-test-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void RouteKeys_RoundTrip_EncryptedAtRest_PerSlot()
    {
        var log = new LogService(Path.Combine(_dir, "logs"));
        const string climate = "plk_0123456789_" + "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90";
        const string flow = "flowmaster-key-example";

        var store = new CredentialService(log, _dir);
        Assert.True(store.SaveRouteKey("block-summary", climate));
        Assert.True(store.SaveRouteKey("flowmaster", flow));

        var file = File.ReadAllBytes(Path.Combine(_dir, "secure-routes.dat"));
        var asText = Encoding.UTF8.GetString(file) + Encoding.Unicode.GetString(file);
        Assert.DoesNotContain("plk_", asText);
        Assert.DoesNotContain(flow, asText);

        // A new instance (an app restart, or an upgraded version) reads the same keys.
        var again = new CredentialService(log, _dir);
        Assert.Equal(climate, again.LoadRouteKey("block-summary"));
        Assert.Equal(flow, again.LoadRouteKey("flowmaster"));
        Assert.True(again.RemoveRouteKey("flowmaster"));
        Assert.Null(new CredentialService(log, _dir).LoadRouteKey("flowmaster"));
        Assert.Equal(climate, new CredentialService(log, _dir).LoadRouteKey("block-summary"));
    }
}
