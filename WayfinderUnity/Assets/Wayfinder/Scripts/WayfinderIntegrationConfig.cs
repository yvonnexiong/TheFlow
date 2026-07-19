using System;

[Serializable]
public sealed class WayfinderIntegrationConfig
{
    public string gatewayBaseUrl = "http://127.0.0.1:8787";
    public bool enableWorldLabs = false;
    public bool enableReactor = false;
    public string worldLabsWorldId = "";
    public int requestTimeoutSeconds = 4;
    public int healthRetrySeconds = 10;

    public void Normalize()
    {
        gatewayBaseUrl = (gatewayBaseUrl ?? string.Empty).Trim().TrimEnd('/');
        worldLabsWorldId = (worldLabsWorldId ?? string.Empty).Trim();
        requestTimeoutSeconds = Math.Max(1, Math.Min(30, requestTimeoutSeconds));
        healthRetrySeconds = Math.Max(2, Math.Min(120, healthRetrySeconds));
    }

    public bool HasGateway =>
        Uri.TryCreate(gatewayBaseUrl, UriKind.Absolute, out Uri uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}

[Serializable]
public sealed class WayfinderGatewayHealth
{
    public bool ok;
    public string mode;
    public bool worldLabsConfigured;
    public bool reactorConfigured;
}

[Serializable]
public sealed class WayfinderWorldLabsResponse
{
    public bool available;
    public string world_id;
    public string pano_url;
    public string marble_url;
    public string error;
}

[Serializable]
public sealed class WayfinderStateEvent
{
    public string state;
    public int progress;
    public int required;
    public bool completed;
    public string input;
    public long unixTime;
}
