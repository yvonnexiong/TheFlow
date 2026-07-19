using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

public sealed class WayfinderIntegrationManager : MonoBehaviour
{
    private const string ConfigFileName = "wayfinder.integration.json";

    [Header("Optional References")]
    [SerializeField] private WayfinderRiverController riverController;
    [SerializeField] private TextMesh statusText;
    [SerializeField] private Renderer worldLabsPanoRenderer;

    private WayfinderIntegrationConfig config = new WayfinderIntegrationConfig();
    private WayfinderGatewayHealth health;
    private WayfinderRiverController.RiverState lastState;
    private int lastProgress = -1;
    private bool initialized;
    private bool gatewayOnline;
    private Coroutine statePost;

    public bool GatewayOnline => gatewayOnline;
    public WayfinderGatewayHealth Health => health;
    public WayfinderIntegrationConfig Config => config;

    private IEnumerator Start()
    {
        if (riverController == null)
        {
            riverController = FindFirstObjectByType<WayfinderRiverController>();
        }

        if (statusText == null)
        {
            GameObject statusObject = GameObject.Find("Integration Status");
            statusText = statusObject != null ? statusObject.GetComponent<TextMesh>() : null;
        }

        SetStatus("DESKTOP  •  OFFLINE SAFE", new Color(0.62f, 0.78f, 0.70f));
        yield return LoadConfig();
        yield return CheckGateway();

        if (gatewayOnline && config.enableWorldLabs && !string.IsNullOrWhiteSpace(config.worldLabsWorldId))
        {
            yield return LoadWorldLabsPano();
        }

        initialized = true;
        if (riverController != null)
        {
            lastState = riverController.State;
            lastProgress = riverController.SuccessfulPushes;
            QueueStatePost();
        }

        StartCoroutine(HealthLoop());
    }

    private void Update()
    {
        if (!initialized || riverController == null)
        {
            return;
        }

        if (lastState != riverController.State || lastProgress != riverController.SuccessfulPushes)
        {
            lastState = riverController.State;
            lastProgress = riverController.SuccessfulPushes;
            QueueStatePost();
        }
    }

    private IEnumerator LoadConfig()
    {
        string path = System.IO.Path.Combine(Application.streamingAssetsPath, ConfigFileName);
        using UnityWebRequest request = UnityWebRequest.Get(path);
        request.timeout = 3;
        yield return request.SendWebRequest();

        if (request.result == UnityWebRequest.Result.Success && !string.IsNullOrWhiteSpace(request.downloadHandler.text))
        {
            try
            {
                WayfinderIntegrationConfig parsed = JsonUtility.FromJson<WayfinderIntegrationConfig>(request.downloadHandler.text);
                if (parsed != null)
                {
                    config = parsed;
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Wayfinder integration config is invalid; using offline-safe defaults. " + exception.Message);
            }
        }

        config.Normalize();
    }

    private IEnumerator CheckGateway()
    {
        gatewayOnline = false;
        health = null;

        if (!config.HasGateway)
        {
            SetOfflineStatus();
            yield break;
        }

        using UnityWebRequest request = UnityWebRequest.Get(config.gatewayBaseUrl + "/health");
        request.timeout = config.requestTimeoutSeconds;
        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            SetOfflineStatus();
            yield break;
        }

        try
        {
            health = JsonUtility.FromJson<WayfinderGatewayHealth>(request.downloadHandler.text);
            gatewayOnline = health != null && health.ok;
        }
        catch (Exception)
        {
            gatewayOnline = false;
        }

        if (!gatewayOnline)
        {
            SetOfflineStatus();
            yield break;
        }

        string world = config.enableWorldLabs && health.worldLabsConfigured ? "WORLD LABS" : "LOCAL WORLD";
        string reactor = config.enableReactor && health.reactorConfigured ? "REACTOR" : "OFFLINE BRANCHES";
        SetStatus(world + "  •  " + reactor, new Color(0.70f, 0.96f, 0.82f));
    }

    private IEnumerator HealthLoop()
    {
        while (true)
        {
            yield return new WaitForSecondsRealtime(config.healthRetrySeconds);
            yield return CheckGateway();
        }
    }

    private void QueueStatePost()
    {
        if (!gatewayOnline || !config.enableReactor || health == null || !health.reactorConfigured)
        {
            return;
        }

        if (statePost != null)
        {
            StopCoroutine(statePost);
        }
        statePost = StartCoroutine(PostCurrentState());
    }

    private IEnumerator PostCurrentState()
    {
        WayfinderStateEvent payload = new WayfinderStateEvent
        {
            state = riverController.State.ToString().ToLowerInvariant(),
            progress = riverController.SuccessfulPushes,
            required = riverController.RequiredPushes,
            completed = riverController.State == WayfinderRiverController.RiverState.Completion,
            input = riverController.IsUsingXRInput ? "xr" : "mouse",
            unixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        byte[] body = Encoding.UTF8.GetBytes(JsonUtility.ToJson(payload));
        using UnityWebRequest request = new UnityWebRequest(config.gatewayBaseUrl + "/api/reactor/event", "POST");
        request.uploadHandler = new UploadHandlerRaw(body);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");
        request.timeout = config.requestTimeoutSeconds;
        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            gatewayOnline = false;
            SetOfflineStatus();
        }
        statePost = null;
    }

    private IEnumerator LoadWorldLabsPano()
    {
        string url = config.gatewayBaseUrl + "/api/worldlabs/world?world_id=" + UnityWebRequest.EscapeURL(config.worldLabsWorldId);
        using UnityWebRequest metadata = UnityWebRequest.Get(url);
        metadata.timeout = config.requestTimeoutSeconds;
        yield return metadata.SendWebRequest();

        if (metadata.result != UnityWebRequest.Result.Success)
        {
            yield break;
        }

        WayfinderWorldLabsResponse world;
        try
        {
            world = JsonUtility.FromJson<WayfinderWorldLabsResponse>(metadata.downloadHandler.text);
        }
        catch (Exception)
        {
            yield break;
        }

        if (world == null || !world.available || string.IsNullOrWhiteSpace(world.pano_url))
        {
            yield break;
        }

        using UnityWebRequest textureRequest = UnityWebRequestTexture.GetTexture(world.pano_url, true);
        textureRequest.timeout = Math.Max(8, config.requestTimeoutSeconds);
        yield return textureRequest.SendWebRequest();

        if (textureRequest.result == UnityWebRequest.Result.Success)
        {
            Texture texture = DownloadHandlerTexture.GetContent(textureRequest);
            if (worldLabsPanoRenderer != null)
            {
                worldLabsPanoRenderer.material.mainTexture = texture;
            }
            else
            {
                Shader skyShader = Shader.Find("Skybox/Panoramic");
                if (skyShader != null)
                {
                    Material skybox = new Material(skyShader) { name = "World Labs Marble Panorama (Runtime)" };
                    skybox.SetTexture("_MainTex", texture);
                    RenderSettings.skybox = skybox;
                    DynamicGI.UpdateEnvironment();
                }
            }
        }
    }

    private void SetOfflineStatus()
    {
        string input = riverController != null && riverController.IsUsingXRInput ? "XR" : "DESKTOP";
        SetStatus(input + "  •  OFFLINE SAFE", new Color(0.62f, 0.78f, 0.70f));
    }

    private void SetStatus(string message, Color color)
    {
        if (statusText == null)
        {
            return;
        }
        statusText.text = message;
        statusText.color = color;
    }
}
