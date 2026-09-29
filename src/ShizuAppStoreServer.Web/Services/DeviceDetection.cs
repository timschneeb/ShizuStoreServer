namespace ShizuAppStoreServer.Web.Services;

/// <summary>Coarse platform detection for the open-in-app action.</summary>
public static class DeviceDetection
{
    public static bool IsAndroid(HttpRequest request) =>
        request.Headers.UserAgent.ToString().Contains("Android", StringComparison.OrdinalIgnoreCase);
}
