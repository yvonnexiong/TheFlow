using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

public static class WayfinderSceneBuilder
{
    private const string ScenePath = "Assets/Wayfinder/Scenes/WayfinderRiver.unity";

    [MenuItem("Wayfinder/Build River Scene")]
    public static void BuildRiverScene()
    {
        Random.InitState(108);
        Directory.CreateDirectory("Assets/Wayfinder/Scenes");

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.28f, 0.37f, 0.35f);
        RenderSettings.fog = true;
        RenderSettings.fogColor = new Color(0.58f, 0.70f, 0.68f);
        RenderSettings.fogDensity = 0.014f;

        Material skyMat = new Material(Shader.Find("Skybox/Procedural"));
        skyMat.name = "Sky_DawnMist";
        skyMat.SetColor("_SkyTint", new Color(0.34f, 0.51f, 0.52f));
        skyMat.SetColor("_GroundColor", new Color(0.13f, 0.20f, 0.19f));
        skyMat.SetFloat("_AtmosphereThickness", 0.72f);
        skyMat.SetFloat("_Exposure", 0.72f);
        RenderSettings.skybox = skyMat;

        Material groundMat = Material("Ground_Moss", new Color(0.19f, 0.29f, 0.22f), 0f, 0.25f);
        Material riverMat = Material("River_Calm", new Color(0.08f, 0.40f, 0.43f, 0.82f), 0f, 0.72f);
        riverMat.SetFloat("_Mode", 3f);
        riverMat.EnableKeyword("_ALPHABLEND_ON");
        riverMat.renderQueue = 3000;
        Material stoneMat = Material("Stone_Charcoal", new Color(0.28f, 0.34f, 0.33f), 0f, 0.34f);
        Material gateMat = Material("Gate_WeatheredStone", new Color(0.42f, 0.45f, 0.41f), 0f, 0.3f);
        Material goldMat = EmissiveMaterial("Gate_GoldGlow", new Color(0.95f, 0.72f, 0.26f), new Color(0.70f, 0.34f, 0.05f), 0.2f);
        Material flowMat = EmissiveMaterial("Flow_Line", new Color(0.66f, 0.96f, 0.82f, 0.72f), new Color(0.22f, 0.68f, 0.48f), 0f);
        Material pathMat = Material("Path_WarmStone", new Color(0.33f, 0.35f, 0.29f), 0f, 0.22f);

        CreateCamera();
        CreateLight();
        CreateGround(groundMat);
        Renderer riverRenderer = CreateRiver(riverMat);
        CreateBanks(groundMat, stoneMat);
        CreateApproachPath(pathMat, goldMat);
        CreateMountains();
        CreateBamboo();

        List<Transform> stones = CreateStones(stoneMat);
        List<LineRenderer> flowLines = CreateFlowLines(flowMat);
        (Transform gateLeft, Transform gateRight, Light gateLight) = CreateGate(gateMat, goldMat);
        CreateHarmonyAccents(goldMat);
        ParticleSystem calmParticles = CreateCalmParticles();
        ParticleSystem turbulenceParticles = CreateTurbulenceParticles();
        LineRenderer gestureTrail = CreateGestureTrail(flowMat);
        (Transform guide, TextMesh textMesh) = CreateGuideText();
        TextMesh legacyInstructions = CreateInstructionPanel();
        TextMesh integrationStatus = CreateIntegrationStatus();

        GameObject controller = new GameObject("Wayfinder_River_Controller");
        var riverController = controller.AddComponent<WayfinderRiverController>();
        SerializedObject serialized = new SerializedObject(riverController);
        serialized.FindProperty("riverRenderer").objectReferenceValue = riverRenderer;
        serialized.FindProperty("gateLight").objectReferenceValue = gateLight;
        serialized.FindProperty("gateLeft").objectReferenceValue = gateLeft;
        serialized.FindProperty("gateRight").objectReferenceValue = gateRight;
        serialized.FindProperty("guideText").objectReferenceValue = guide;
        serialized.FindProperty("guideTextMesh").objectReferenceValue = textMesh;
        serialized.FindProperty("calmParticles").objectReferenceValue = calmParticles;
        serialized.FindProperty("turbulenceParticles").objectReferenceValue = turbulenceParticles;
        serialized.FindProperty("gestureTrail").objectReferenceValue = gestureTrail;
        SerializedProperty stoneList = serialized.FindProperty("stones");
        stoneList.arraySize = stones.Count;
        for (int i = 0; i < stones.Count; i++)
        {
            stoneList.GetArrayElementAtIndex(i).objectReferenceValue = stones[i];
        }
        SerializedProperty flowList = serialized.FindProperty("flowLines");
        flowList.arraySize = flowLines.Count;
        for (int i = 0; i < flowLines.Count; i++)
        {
            flowList.GetArrayElementAtIndex(i).objectReferenceValue = flowLines[i];
        }
        serialized.ApplyModifiedPropertiesWithoutUndo();

        GameObject integrationObject = new GameObject("Wayfinder_Integration_Manager");
        var integrationManager = integrationObject.AddComponent<WayfinderIntegrationManager>();
        SerializedObject integrationSerialized = new SerializedObject(integrationManager);
        integrationSerialized.FindProperty("riverController").objectReferenceValue = riverController;
        integrationSerialized.FindProperty("statusText").objectReferenceValue = integrationStatus;
        integrationSerialized.FindProperty("worldLabsPanoRenderer").objectReferenceValue = null;
        integrationSerialized.ApplyModifiedPropertiesWithoutUndo();

        CreateVerticalSlice(riverController);
        guide.gameObject.SetActive(false);
        gestureTrail.gameObject.SetActive(false);
        legacyInstructions.gameObject.SetActive(false);
        integrationStatus.gameObject.SetActive(false);

        EditorSceneManager.SaveScene(scene, ScenePath);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        AssetDatabase.SaveAssets();
        Debug.Log("Wayfinder scene generated at " + ScenePath);
    }

    public static void CapturePreview()
    {
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        Camera camera = Camera.main;
        if (camera == null)
        {
            Debug.LogError("Wayfinder preview capture failed: no Main Camera in scene.");
            EditorApplication.Exit(1);
            return;
        }

        const int width = 1280;
        const int height = 720;
        var renderTexture = new RenderTexture(width, height, 24);
        var texture = new Texture2D(width, height, TextureFormat.RGB24, false);

        camera.targetTexture = renderTexture;
        camera.Render();
        RenderTexture.active = renderTexture;
        texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
        texture.Apply();

        Directory.CreateDirectory("../artifacts");
        File.WriteAllBytes("../artifacts/wayfinder-preview.png", texture.EncodeToPNG());

        camera.targetTexture = null;
        RenderTexture.active = null;
        Object.DestroyImmediate(renderTexture);
        Object.DestroyImmediate(texture);
        Debug.Log("Wayfinder preview saved to artifacts/wayfinder-preview.png");
    }

    private static Material Material(string name, Color color, float metallic, float smoothness)
    {
        var material = new Material(Shader.Find("Standard"));
        material.name = name;
        material.color = color;
        material.SetFloat("_Metallic", metallic);
        material.SetFloat("_Glossiness", smoothness);
        return material;
    }

    private static Material EmissiveMaterial(string name, Color color, Color emission, float smoothness)
    {
        Material material = Material(name, color, 0f, smoothness);
        material.EnableKeyword("_EMISSION");
        material.SetColor("_EmissionColor", emission);
        return material;
    }

    private static void CreateCamera()
    {
        GameObject cameraObject = new GameObject("Main Camera");
        Camera camera = cameraObject.AddComponent<Camera>();
        cameraObject.tag = "MainCamera";
        camera.transform.position = new Vector3(0f, 5.15f, -9.6f);
        camera.transform.rotation = Quaternion.Euler(28f, 0f, 0f);
        camera.fieldOfView = 44f;
        camera.clearFlags = CameraClearFlags.Skybox;
        camera.backgroundColor = new Color(0.50f, 0.60f, 0.60f);
        cameraObject.AddComponent<AudioListener>();
        cameraObject.AddComponent<WayfinderXRHeadDriver>();
    }

    private static void CreateLight()
    {
        GameObject lightObject = new GameObject("Soft Sun");
        Light light = lightObject.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.1f;
        light.color = new Color(1f, 0.94f, 0.82f);
        light.transform.rotation = Quaternion.Euler(46f, -31f, 0f);
        light.shadows = LightShadows.Soft;
        light.shadowStrength = 0.55f;

        GameObject fillObject = new GameObject("River Jade Fill");
        Light fill = fillObject.AddComponent<Light>();
        fill.type = LightType.Point;
        fillObject.transform.position = new Vector3(-3.8f, 3.4f, -0.5f);
        fill.color = new Color(0.45f, 0.80f, 0.72f);
        fill.intensity = 0.9f;
        fill.range = 9f;
    }

    private static void CreateGround(Material material)
    {
        GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
        ground.name = "Moss Ground";
        ground.transform.position = new Vector3(0f, -0.24f, 2.1f);
        ground.transform.localScale = new Vector3(18f, 0.35f, 18f);
        ground.GetComponent<Renderer>().sharedMaterial = material;
    }

    private static Renderer CreateRiver(Material material)
    {
        GameObject river = GameObject.CreatePrimitive(PrimitiveType.Cube);
        river.name = "Reactive River";
        river.transform.position = new Vector3(0f, 0.02f, 0.65f);
        river.transform.localScale = new Vector3(15.8f, 0.08f, 3.5f);
        Renderer renderer = river.GetComponent<Renderer>();
        renderer.sharedMaterial = material;
        return renderer;
    }

    private static void CreateBanks(Material groundMat, Material stoneMat)
    {
        for (int i = 0; i < 2; i++)
        {
            float z = i == 0 ? -1.45f : 2.85f;
            GameObject bank = GameObject.CreatePrimitive(PrimitiveType.Cube);
            bank.name = i == 0 ? "Near Riverbank" : "Far Riverbank";
            bank.transform.position = new Vector3(0f, 0.06f, z);
            bank.transform.localScale = new Vector3(16f, 0.35f, 0.7f);
            bank.GetComponent<Renderer>().sharedMaterial = groundMat;
        }

        for (int i = 0; i < 18; i++)
        {
            GameObject rock = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            rock.name = "Riverbank Rock";
            float side = i % 2 == 0 ? -1f : 1f;
            rock.transform.position = new Vector3(Random.Range(-7f, 7f), 0.2f, side < 0 ? Random.Range(-1.8f, -1.2f) : Random.Range(2.4f, 3.1f));
            rock.transform.localScale = new Vector3(Random.Range(0.25f, 0.7f), Random.Range(0.08f, 0.22f), Random.Range(0.2f, 0.55f));
            rock.GetComponent<Renderer>().sharedMaterial = stoneMat;
        }
    }

    private static void CreateApproachPath(Material pathMat, Material goldMat)
    {
        for (int i = 0; i < 9; i++)
        {
            float t = i / 8f;
            GameObject slab = GameObject.CreatePrimitive(PrimitiveType.Cube);
            slab.name = "Quiet Approach Marker";
            slab.transform.position = new Vector3(Mathf.Sin(t * Mathf.PI) * 0.16f, 0.005f, Mathf.Lerp(-4.2f, -1.68f, t));
            slab.transform.localScale = new Vector3(Mathf.Lerp(1.5f, 1.05f, t), 0.08f, 0.22f);
            slab.transform.rotation = Quaternion.Euler(0f, Mathf.Sin(t * Mathf.PI * 2f) * 4f, 0f);
            slab.GetComponent<Renderer>().sharedMaterial = pathMat;
        }

        for (int i = 0; i < 5; i++)
        {
            GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = "Distant Golden Waypoint";
            marker.transform.position = new Vector3(0f, 0.13f, 3.05f + i * 0.24f);
            marker.transform.localScale = Vector3.one * (0.055f + i * 0.012f);
            marker.GetComponent<Renderer>().sharedMaterial = goldMat;
        }
    }

    private static void CreateMountains()
    {
        Material mountainMat = Material("Distant_Mountain", new Color(0.19f, 0.28f, 0.28f), 0f, 0.16f);
        for (int i = 0; i < 8; i++)
        {
            GameObject mountain = CreateCone("Distant Mountain");
            mountain.name = "Distant Mountain";
            mountain.transform.position = new Vector3(-8f + i * 2.3f, 1.0f + Random.Range(-0.2f, 0.3f), 8.4f + Random.Range(-0.5f, 0.7f));
            float scale = Random.Range(2.1f, 3.8f);
            mountain.transform.localScale = new Vector3(scale, Random.Range(2.4f, 4.1f), scale);
            mountain.GetComponent<Renderer>().sharedMaterial = mountainMat;
        }
    }

    private static GameObject CreateCone(string name)
    {
        GameObject cone = new GameObject(name);
        var meshFilter = cone.AddComponent<MeshFilter>();
        cone.AddComponent<MeshRenderer>();

        const int sides = 18;
        var vertices = new Vector3[sides + 2];
        var triangles = new int[sides * 6];
        vertices[0] = Vector3.up;
        vertices[1] = Vector3.zero;

        for (int i = 0; i < sides; i++)
        {
            float angle = (Mathf.PI * 2f * i) / sides;
            vertices[i + 2] = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
        }

        int triangleIndex = 0;
        for (int i = 0; i < sides; i++)
        {
            int current = i + 2;
            int next = i == sides - 1 ? 2 : i + 3;
            triangles[triangleIndex++] = 0;
            triangles[triangleIndex++] = next;
            triangles[triangleIndex++] = current;
            triangles[triangleIndex++] = 1;
            triangles[triangleIndex++] = current;
            triangles[triangleIndex++] = next;
        }

        var mesh = new Mesh();
        mesh.name = name + " Mesh";
        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        meshFilter.sharedMesh = mesh;
        return cone;
    }

    private static void CreateBamboo()
    {
        Material bambooMat = Material("Quiet_Bamboo", new Color(0.28f, 0.43f, 0.30f), 0f, 0.2f);
        for (int i = 0; i < 22; i++)
        {
            GameObject stalk = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            stalk.name = "Minimal Bamboo";
            float x = Random.value < 0.5f ? Random.Range(-8f, -5.7f) : Random.Range(5.7f, 8f);
            stalk.transform.position = new Vector3(x, 1.0f, Random.Range(-3.6f, 5.6f));
            stalk.transform.localScale = new Vector3(0.055f, Random.Range(0.9f, 1.7f), 0.055f);
            stalk.GetComponent<Renderer>().sharedMaterial = bambooMat;

            if (i % 2 == 0)
            {
                GameObject leaf = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                leaf.name = "Bamboo Leaf Cluster";
                leaf.transform.position = stalk.transform.position + new Vector3(i % 4 == 0 ? 0.18f : -0.18f, stalk.transform.localScale.y * 0.72f, 0f);
                leaf.transform.localScale = new Vector3(0.38f, 0.12f, 0.18f);
                leaf.transform.rotation = Quaternion.Euler(0f, Random.Range(-35f, 35f), Random.Range(-18f, 18f));
                leaf.GetComponent<Renderer>().sharedMaterial = bambooMat;
            }
        }
    }

    private static List<Transform> CreateStones(Material material)
    {
        var stones = new List<Transform>();
        float[] xPositions = { -0.58f, 0.08f, 0.54f };
        for (int i = 0; i < 3; i++)
        {
            GameObject stone = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            stone.name = "Rising Stone " + (i + 1);
            float t = i / 2f;
            stone.transform.position = new Vector3(xPositions[i], -0.72f, Mathf.Lerp(-0.95f, 2.18f, t));
            stone.transform.localScale = new Vector3(
                1.05f + i * 0.12f, 0.18f + i * 0.025f, 0.82f + i * 0.07f);
            stone.transform.rotation = Quaternion.Euler(0f, Mathf.Lerp(-11f, 13f, Mathf.PingPong(i * 0.47f, 1f)), 0f);
            stone.GetComponent<Renderer>().sharedMaterial = material;
            stones.Add(stone.transform);
        }

        return stones;
    }

    private static List<LineRenderer> CreateFlowLines(Material material)
    {
        var lines = new List<LineRenderer>();
        for (int lineIndex = 0; lineIndex < 5; lineIndex++)
        {
            GameObject lineObject = new GameObject("Glowing Flow Line " + (lineIndex + 1));
            LineRenderer line = lineObject.AddComponent<LineRenderer>();
            line.sharedMaterial = material;
            line.positionCount = 28;
            line.widthMultiplier = lineIndex == 2 ? 0.055f : 0.032f;
            line.useWorldSpace = true;
            line.numCapVertices = 4;
            line.startColor = new Color(0.66f, 0.96f, 0.82f, 0.62f);
            line.endColor = new Color(0.66f, 0.96f, 0.82f, 0.16f);
            float offset = (lineIndex - 2f) * 0.43f;
            for (int i = 0; i < 28; i++)
            {
                float t = i / 27f;
                line.SetPosition(i, new Vector3(Mathf.Lerp(-6.4f, 6.4f, t), 0.14f + lineIndex * 0.012f, 0.58f + offset + Mathf.Sin(t * Mathf.PI * 2f + lineIndex * 0.7f) * 0.16f));
            }
            lines.Add(line);
        }

        return lines;
    }

    private static (Transform, Transform, Light) CreateGate(Material gateMat, Material goldMat)
    {
        GameObject group = new GameObject("Golden Gate");
        GameObject left = Pillar("Gate Left Pillar", new Vector3(-1.25f, 1.35f, 4.25f), gateMat);
        GameObject right = Pillar("Gate Right Pillar", new Vector3(1.25f, 1.35f, 4.25f), gateMat);
        left.transform.parent = group.transform;
        right.transform.parent = group.transform;

        GameObject beam = GameObject.CreatePrimitive(PrimitiveType.Cube);
        beam.name = "Gate Top Beam";
        beam.transform.position = new Vector3(0f, 2.75f, 4.25f);
        beam.transform.localScale = new Vector3(3.2f, 0.32f, 0.42f);
        beam.GetComponent<Renderer>().sharedMaterial = gateMat;
        beam.transform.parent = group.transform;

        GameObject disk = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        disk.name = "Subtle Harmony Orb";
        disk.transform.position = new Vector3(0f, 2.18f, 4.02f);
        disk.transform.localScale = new Vector3(0.55f, 0.55f, 0.09f);
        disk.GetComponent<Renderer>().sharedMaterial = goldMat;
        disk.transform.parent = group.transform;

        GameObject lightObject = new GameObject("Gate Warm Glow");
        lightObject.transform.position = new Vector3(0f, 1.65f, 3.55f);
        Light light = lightObject.AddComponent<Light>();
        light.type = LightType.Point;
        light.color = new Color(1f, 0.78f, 0.35f);
        light.intensity = 0.25f;
        light.range = 3.2f;

        return (left.transform, right.transform, light);
    }

    private static void CreateHarmonyAccents(Material goldMat)
    {
        CreateRing("Gate Harmony Halo", new Vector3(0f, 2.18f, 4.08f), 0.88f, 48, 0.026f, goldMat);

        GameObject threshold = GameObject.CreatePrimitive(PrimitiveType.Cube);
        threshold.name = "Gate Golden Threshold";
        threshold.transform.position = new Vector3(0f, 0.12f, 3.86f);
        threshold.transform.localScale = new Vector3(2.05f, 0.08f, 0.11f);
        threshold.GetComponent<Renderer>().sharedMaterial = goldMat;
    }

    private static void CreateRing(string name, Vector3 center, float radius, int segments, float width, Material material)
    {
        GameObject ringObject = new GameObject(name);
        LineRenderer ring = ringObject.AddComponent<LineRenderer>();
        ring.sharedMaterial = material;
        ring.useWorldSpace = true;
        ring.loop = true;
        ring.positionCount = segments;
        ring.widthMultiplier = width;
        ring.numCornerVertices = 3;
        ring.numCapVertices = 3;
        ring.startColor = Color.white;
        ring.endColor = new Color(1f, 1f, 1f, 0.82f);
        for (int i = 0; i < segments; i++)
        {
            float angle = (Mathf.PI * 2f * i) / segments;
            ring.SetPosition(i, center + new Vector3(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius, 0f));
        }
    }

    private static GameObject Pillar(string name, Vector3 position, Material material)
    {
        GameObject pillar = GameObject.CreatePrimitive(PrimitiveType.Cube);
        pillar.name = name;
        pillar.transform.position = position;
        pillar.transform.localScale = new Vector3(0.38f, 2.7f, 0.48f);
        pillar.GetComponent<Renderer>().sharedMaterial = material;
        return pillar;
    }

    private static ParticleSystem CreateCalmParticles()
    {
        GameObject particleObject = new GameObject("Calm Flow Particles");
        particleObject.transform.position = new Vector3(0f, 0.35f, 0.65f);
        ParticleSystem particles = particleObject.AddComponent<ParticleSystem>();
        ParticleSystem.MainModule main = particles.main;
        main.startLifetime = 2.2f;
        main.startSpeed = 0.75f;
        main.startSize = 0.06f;
        main.startColor = new Color(0.82f, 1f, 0.92f, 0.42f);
        ParticleSystem.EmissionModule emission = particles.emission;
        emission.rateOverTime = 10f;
        ParticleSystem.ShapeModule shape = particles.shape;
        shape.shapeType = ParticleSystemShapeType.Box;
        shape.scale = new Vector3(10f, 0.1f, 2.4f);
        ParticleSystem.VelocityOverLifetimeModule velocity = particles.velocityOverLifetime;
        velocity.enabled = true;
        velocity.x = 0.55f;
        return particles;
    }

    private static ParticleSystem CreateTurbulenceParticles()
    {
        GameObject particleObject = new GameObject("Turbulence Splash Particles");
        particleObject.transform.position = new Vector3(0f, 0.42f, 0.65f);
        ParticleSystem particles = particleObject.AddComponent<ParticleSystem>();
        ParticleSystem.MainModule main = particles.main;
        main.startLifetime = 0.65f;
        main.startSpeed = 2.4f;
        main.startSize = 0.08f;
        main.startColor = new Color(0.90f, 0.97f, 1f, 0.65f);
        ParticleSystem.EmissionModule emission = particles.emission;
        emission.rateOverTime = 0f;
        ParticleSystem.ShapeModule shape = particles.shape;
        shape.shapeType = ParticleSystemShapeType.Box;
        shape.scale = new Vector3(11f, 0.1f, 2.6f);
        return particles;
    }

    private static LineRenderer CreateGestureTrail(Material material)
    {
        GameObject trailObject = new GameObject("Tai Chi Inspired Gesture Trail");
        LineRenderer trail = trailObject.AddComponent<LineRenderer>();
        trail.sharedMaterial = material;
        trail.widthMultiplier = 0.075f;
        trail.numCapVertices = 5;
        trail.useWorldSpace = true;
        return trail;
    }

    private static (Transform, TextMesh) CreateGuideText()
    {
        GameObject textObject = new GameObject("Spatial Guide Text");
        textObject.transform.position = new Vector3(0f, 3.35f, 1.10f);
        textObject.transform.rotation = Quaternion.Euler(6f, 0f, 0f);
        TextMesh text = textObject.AddComponent<TextMesh>();
        text.text = "Hold. Push slowly from left to right.\nRelease, rest, and follow the river's rhythm.";
        text.fontSize = 64;
        text.characterSize = 0.052f;
        text.lineSpacing = 1.08f;
        text.anchor = TextAnchor.MiddleCenter;
        text.alignment = TextAlignment.Center;
        text.color = new Color(0.94f, 1f, 0.96f);
        return (textObject.transform, text);
    }

    private static TextMesh CreateInstructionPanel()
    {
        GameObject textObject = new GameObject("Demo Controls");
        textObject.transform.position = new Vector3(-0.65f, 1.12f, -3.10f);
        textObject.transform.rotation = Quaternion.Euler(10f, 0f, 0f);
        TextMesh text = textObject.AddComponent<TextMesh>();
        text.text = "RUSH  •  THE RIVER RESISTS\nHOLD + SLOW PUSH  •  RELEASE + REST";
        text.fontSize = 46;
        text.characterSize = 0.040f;
        text.lineSpacing = 1.08f;
        text.anchor = TextAnchor.MiddleCenter;
        text.alignment = TextAlignment.Center;
        text.color = new Color(0.86f, 1f, 0.93f);
        return text;
    }

    private static TextMesh CreateIntegrationStatus()
    {
        GameObject statusObject = new GameObject("Integration Status");
        statusObject.transform.position = new Vector3(3.45f, 0.72f, -2.95f);
        statusObject.transform.rotation = Quaternion.Euler(8f, -8f, 0f);
        TextMesh status = statusObject.AddComponent<TextMesh>();
        status.text = "DESKTOP  •  OFFLINE SAFE";
        status.fontSize = 44;
        status.characterSize = 0.030f;
        status.anchor = TextAnchor.MiddleRight;
        status.alignment = TextAlignment.Right;
        status.color = new Color(0.64f, 0.79f, 0.74f, 0.82f);
        return status;
    }

    private static void CreateVerticalSlice(WayfinderRiverController riverController)
    {
        Material panelMaterial = Material("Vertical Slice Panel", new Color(0.035f, 0.09f, 0.09f, 0.88f), 0f, 0.15f);
        Material jadeGlow = EmissiveMaterial("Palm Dwell Jade", new Color(0.24f, 0.78f, 0.68f),
            new Color(0.12f, 0.55f, 0.42f), 0.45f);
        Material goldGlow = EmissiveMaterial("Memory Gold", new Color(0.96f, 0.72f, 0.25f),
            new Color(0.72f, 0.36f, 0.05f), 0.55f);
        Material memoryGlow = EmissiveMaterial("WorldLabs Memory Slot", new Color(0.32f, 0.82f, 0.88f),
            new Color(0.10f, 0.48f, 0.60f), 0.5f);

        GameObject handVisualObject = new GameObject("XR Hands Procedural Visualization");
        WayfinderXRHandVisualizer handVisualizer = handVisualObject.AddComponent<WayfinderXRHandVisualizer>();

        GameObject introRoot = new GameObject("Wayfinder Opening Story Card");
        introRoot.transform.SetParent(Camera.main.transform, false);
        introRoot.transform.localPosition = new Vector3(0f, 0f, 1.05f);
        CreateOpeningLogo(introRoot.transform);
        TextMesh introText = CreateLegibleText(
            "What To Expect", Vector3.zero, 54, 0.0055f,
            TextAnchor.MiddleCenter, TextAlignment.Center, new Color(0.95f, 1f, 0.96f));
        introText.transform.SetParent(introRoot.transform, false);
        introText.transform.localPosition = new Vector3(0f, -0.07f, 0f);
        introText.text = WayfinderVerticalSliceController.IntroCopy(0f, false);
        CreatePanelBehind(introRoot.transform, new Vector3(1.22f, 0.82f, 0.04f), panelMaterial);
        Transform introButton = CreateOpenGameButton(introRoot.transform, goldGlow);

        GameObject stoneStoryRoot = new GameObject("Three Stone Story Chapter Card");
        stoneStoryRoot.transform.SetParent(Camera.main.transform, false);
        stoneStoryRoot.transform.localPosition = new Vector3(0f, 0.03f, 1.12f);
        TextMesh stoneStoryText = CreateLegibleText(
            "Stone Story", Vector3.zero, 68, 0.008f,
            TextAnchor.MiddleCenter, TextAlignment.Center, new Color(1f, 0.90f, 0.56f));
        stoneStoryText.transform.SetParent(stoneStoryRoot.transform, false);
        stoneStoryText.text = WayfinderVerticalSliceController.StoneStory(1);
        CreatePanelBehind(stoneStoryRoot.transform, new Vector3(1.12f, 0.54f, 0.04f), panelMaterial);
        stoneStoryRoot.SetActive(false);

        (GameObject peaceRoot, TextMesh peaceText) =
            CreatePeaceStatePresentation(panelMaterial, jadeGlow, goldGlow);

        GameObject feedbackRoot = new GameObject("Head-Locked Normal Feedback");
        feedbackRoot.transform.SetParent(Camera.main.transform, false);
        feedbackRoot.transform.localPosition = new Vector3(0f, 0.10f, 1.35f);

        TextMesh userFeedback = CreateLegibleText(
            "User Feedback HUD", Vector3.zero, 62, 0.008f,
            TextAnchor.MiddleCenter, TextAlignment.Center, new Color(0.94f, 1f, 0.97f));
        userFeedback.transform.SetParent(feedbackRoot.transform, false);
        userFeedback.transform.localPosition = new Vector3(0f, 0.20f, 0f);
        userFeedback.text = "MOVE THE LIGHT WHERE YOUR CIRCLE FEELS COMFORTABLE";
        CreatePanelBehind(userFeedback.transform, new Vector3(1.05f, 0.28f, 0.04f), panelMaterial);

        GameObject judgeRoot = new GameObject("Judge Debug HUD (Palm Toggle)");
        judgeRoot.transform.position = new Vector3(-1.72f, 1.83f, -1.67f);
        judgeRoot.transform.rotation = Quaternion.Euler(0f, 10f, 0f);
        TextMesh judgeHud = CreateLegibleText(
            "Numeric Telemetry", Vector3.zero, 54, 0.017f,
            TextAnchor.MiddleLeft, TextAlignment.Left, new Color(0.70f, 0.95f, 0.86f));
        judgeHud.transform.SetParent(judgeRoot.transform, false);
        judgeHud.text = "TRACKING --\nSPEED 0.000 m/s\nSMOOTH 0.00  CONT 0.00\nFOCUS 0.00\nSTATE AwaitingHands";
        CreatePanelBehind(judgeRoot.transform, new Vector3(2.55f, 1.42f, 0.05f), panelMaterial);
        judgeRoot.SetActive(false);

        TextMesh completion = CreateLegibleText(
            "Memory Saved Confirmation", new Vector3(0f, 2.05f, 0.30f), 72, 0.032f,
            TextAnchor.MiddleCenter, TextAlignment.Center, new Color(1f, 0.86f, 0.42f));
        completion.text = "MEMORY SAVED\nYOUR REWARD IS READY";
        completion.gameObject.SetActive(false);

        WayfinderAnalogSpeedometer speedometer = CreateAnalogSpeedometer(feedbackRoot.transform, jadeGlow, goldGlow);
        WayfinderCircularPalmGuide circularGuide = CreateCircularPalmGuide(jadeGlow, goldGlow);

        WayfinderWorldRevealSlot worldSlot = CreateWorldLabsPlaceholderSlots(memoryGlow, goldGlow);
        WayfinderRewardMaterializationSlot rewardSlot = CreateTripoPlaceholderSlot(goldGlow);
        WayfinderPalmDwellTarget[] targets = CreateDwellTargets(jadeGlow, goldGlow);

        GameObject controllerObject = new GameObject("Wayfinder Memory Vertical Slice");
        WayfinderVerticalSliceController controller = controllerObject.AddComponent<WayfinderVerticalSliceController>();
        SerializedObject serialized = new SerializedObject(controller);
        serialized.FindProperty("riverController").objectReferenceValue = riverController;
        serialized.FindProperty("palmPoseSource").objectReferenceValue = handVisualizer;
        serialized.FindProperty("worldLabsWorldSlot").objectReferenceValue = worldSlot;
        serialized.FindProperty("tripoRewardSlot").objectReferenceValue = rewardSlot;
        serialized.FindProperty("userFeedbackText").objectReferenceValue = userFeedback;
        serialized.FindProperty("judgeHudText").objectReferenceValue = judgeHud;
        serialized.FindProperty("judgeHudRoot").objectReferenceValue = judgeRoot;
        serialized.FindProperty("completionText").objectReferenceValue = completion;
        serialized.FindProperty("analogSpeedometer").objectReferenceValue = speedometer;
        serialized.FindProperty("circularPalmGuide").objectReferenceValue = circularGuide;
        serialized.FindProperty("introCardRoot").objectReferenceValue = introRoot;
        serialized.FindProperty("introCardText").objectReferenceValue = introText;
        serialized.FindProperty("gameplayHudRoot").objectReferenceValue = feedbackRoot;
        serialized.FindProperty("introStartButton").objectReferenceValue = introButton;
        serialized.FindProperty("stoneStoryRoot").objectReferenceValue = stoneStoryRoot;
        serialized.FindProperty("stoneStoryText").objectReferenceValue = stoneStoryText;
        serialized.FindProperty("peaceStateRoot").objectReferenceValue = peaceRoot;
        serialized.FindProperty("peaceStateText").objectReferenceValue = peaceText;
        SerializedProperty dwellArray = serialized.FindProperty("dwellTargets");
        dwellArray.arraySize = targets.Length;
        for (int index = 0; index < targets.Length; index++)
        {
            dwellArray.GetArrayElementAtIndex(index).objectReferenceValue = targets[index];
        }
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }

    private static void CreateOpeningLogo(Transform parent)
    {
        const string logoPath = "Assets/Wayfinder/Art/the-flow-three-stones-logo.png";
        Texture2D logoTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(logoPath);
        if (logoTexture == null)
        {
            Debug.LogWarning("Wayfinder opening logo is missing at " + logoPath);
            return;
        }

        GameObject logo = GameObject.CreatePrimitive(PrimitiveType.Quad);
        logo.name = "Generated THE FLOW Three-Stone Logo";
        logo.transform.SetParent(parent, false);
        logo.transform.localPosition = new Vector3(0f, 0.28f, -0.012f);
        logo.transform.localScale = Vector3.one * 0.28f;
        UnityEngine.Object.DestroyImmediate(logo.GetComponent<Collider>());
        Shader shader = Shader.Find("Unlit/Texture") ?? Shader.Find("Standard");
        Material logoMaterial = new Material(shader) { name = "THE FLOW Generated Logo Material" };
        logoMaterial.mainTexture = logoTexture;
        logo.GetComponent<Renderer>().sharedMaterial = logoMaterial;
    }

    private static Transform CreateOpenGameButton(Transform parent, Material material)
    {
        GameObject button = GameObject.CreatePrimitive(PrimitiveType.Cube);
        button.name = "OPEN THE GAME • Hand Touch Button";
        button.transform.SetParent(parent, false);
        // The copy stays at a comfortable reading distance; the button projects
        // into the PICO hand interaction volume so a natural forward tap reaches it.
        button.transform.localPosition = new Vector3(0f, -0.34f, -0.35f);
        button.transform.localScale = new Vector3(0.48f, 0.095f, 0.025f);
        button.GetComponent<Renderer>().sharedMaterial = material;
        UnityEngine.Object.DestroyImmediate(button.GetComponent<Collider>());

        TextMesh label = CreateLegibleText(
            "OPEN THE GAME Label", Vector3.zero, 58, 0.007f,
            TextAnchor.MiddleCenter, TextAlignment.Center, new Color(0.06f, 0.13f, 0.12f));
        label.text = "OPEN THE GAME";
        label.transform.SetParent(button.transform, false);
        label.transform.localPosition = new Vector3(0f, 0f, -0.55f);
        label.transform.localScale = new Vector3(2.08f, 10.5f, 1f);
        return button.transform;
    }

    private static (GameObject, TextMesh) CreatePeaceStatePresentation(
        Material panelMaterial, Material jadeMaterial, Material goldMaterial)
    {
        GameObject root = new GameObject("PEACE STATE • Unmistakable Completion Reveal");
        root.transform.SetParent(Camera.main.transform, false);
        root.transform.localPosition = new Vector3(0f, 0f, 1.12f);

        for (int ringIndex = 0; ringIndex < 3; ringIndex++)
        {
            GameObject ringObject = new GameObject("Peace Portal Halo " + (ringIndex + 1));
            ringObject.transform.SetParent(root.transform, false);
            LineRenderer ring = ringObject.AddComponent<LineRenderer>();
            ring.sharedMaterial = ringIndex % 2 == 0 ? goldMaterial : jadeMaterial;
            ring.useWorldSpace = false;
            ring.loop = true;
            ring.positionCount = 72;
            ring.widthMultiplier = 0.008f + ringIndex * 0.004f;
            ring.numCornerVertices = 5;
            float radius = 0.34f + ringIndex * 0.08f;
            for (int point = 0; point < ring.positionCount; point++)
            {
                float angle = point / (float)ring.positionCount * Mathf.PI * 2f;
                ring.SetPosition(point, new Vector3(
                    Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius, 0.025f));
            }
        }

        TextMesh text = CreateLegibleText(
            "Peace State Reached", Vector3.zero, 76, 0.010f,
            TextAnchor.MiddleCenter, TextAlignment.Center, new Color(1f, 0.88f, 0.42f));
        text.transform.SetParent(root.transform, false);
        text.text = "PEACE STATE REACHED\n\nTHE GATE IS OPEN\nYOUR MEMORY WORLD IS FORMING";
        CreatePanelBehind(root.transform, new Vector3(1.18f, 0.62f, 0.045f), panelMaterial);
        root.SetActive(false);
        return (root, text);
    }

    private static WayfinderAnalogSpeedometer CreateAnalogSpeedometer(
        Transform feedbackRoot, Material arcMaterial, Material needleMaterial)
    {
        GameObject root = new GameObject("Analog Speedometer • 0 REST 1 FLOW 2 RUSH");
        root.transform.SetParent(feedbackRoot, false);
        root.transform.localPosition = new Vector3(0f, -0.12f, -0.03f);

        LineRenderer arc = root.AddComponent<LineRenderer>();
        arc.sharedMaterial = arcMaterial;
        arc.useWorldSpace = false;
        arc.widthMultiplier = 0.012f;
        arc.numCapVertices = 3;
        arc.positionCount = 33;
        for (int index = 0; index < arc.positionCount; index++)
        {
            float angle = Mathf.Lerp(Mathf.PI, 0f, index / 32f);
            arc.SetPosition(index, new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f) * 0.18f);
        }

        GameObject needle = new GameObject("Damped Speed Needle");
        needle.transform.SetParent(root.transform, false);
        LineRenderer needleLine = needle.AddComponent<LineRenderer>();
        needleLine.sharedMaterial = needleMaterial;
        needleLine.useWorldSpace = false;
        needleLine.widthMultiplier = 0.018f;
        needleLine.numCapVertices = 4;
        needleLine.positionCount = 2;
        needleLine.SetPosition(0, Vector3.zero);
        needleLine.SetPosition(1, new Vector3(0.15f, 0f, -0.01f));
        needle.transform.localRotation = Quaternion.Euler(0f, 0f, 180f);

        CreateGaugeLabel(root.transform, "0 REST", new Vector3(-0.24f, -0.035f, 0f));
        CreateGaugeLabel(root.transform, "1 FLOW", new Vector3(0f, 0.225f, 0f));
        CreateGaugeLabel(root.transform, "2 RUSH", new Vector3(0.24f, -0.035f, 0f));

        WayfinderAnalogSpeedometer speedometer = root.AddComponent<WayfinderAnalogSpeedometer>();
        speedometer.Configure(needle.transform);
        return speedometer;
    }

    private static void CreateGaugeLabel(Transform parent, string copy, Vector3 localPosition)
    {
        TextMesh label = CreateLegibleText(
            "Speedometer " + copy, Vector3.zero, 38, 0.004f,
            TextAnchor.MiddleCenter, TextAlignment.Center, new Color(0.82f, 0.96f, 0.91f));
        label.text = copy;
        label.transform.SetParent(parent, false);
        label.transform.localPosition = localPosition;
    }

    private static WayfinderCircularPalmGuide CreateCircularPalmGuide(
        Material faintMaterial, Material fillMaterial)
    {
        GameObject root = new GameObject("Palm Circle Guide • Calm Analog Clock");
        LineRenderer faint = root.AddComponent<LineRenderer>();
        faint.sharedMaterial = faintMaterial;
        faint.useWorldSpace = true;
        faint.widthMultiplier = 0.05f;
        faint.numCapVertices = 8;
        faint.numCornerVertices = 5;
        faint.startColor = faint.endColor = new Color(0.35f, 0.93f, 0.82f, 0.25f);

        GameObject fillObject = new GameObject("Valid Flow Clock Fill");
        fillObject.transform.SetParent(root.transform, false);
        LineRenderer fill = fillObject.AddComponent<LineRenderer>();
        fill.sharedMaterial = fillMaterial;
        fill.useWorldSpace = true;
        fill.widthMultiplier = 0.022f;
        fill.numCapVertices = 4;
        fill.startColor = new Color(0.98f, 0.78f, 0.28f, 0.95f);
        fill.endColor = new Color(0.35f, 1f, 0.82f, 0.95f);

        LineRenderer halo = CreateEnergyLine(
            root.transform, "Calm Pearl Hold Progress Halo", fillMaterial, 0.010f,
            new Color(1f, 0.82f, 0.38f, 0.92f));
        LineRenderer tealRibbon = CreateEnergyLine(
            root.transform, "Persistent Teal Water-Ink Ribbon", faintMaterial, 0.028f,
            new Color(0.22f, 0.96f, 0.82f, 0.82f));
        LineRenderer goldRibbon = CreateEnergyLine(
            root.transform, "Persistent Warm-Gold Calligraphy Ribbon", fillMaterial, 0.012f,
            new Color(1f, 0.74f, 0.26f, 0.88f));

        GameObject pearl = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        pearl.name = "Palm-Attached Luminous Placement Pearl";
        pearl.transform.SetParent(root.transform, false);
        pearl.GetComponent<Renderer>().sharedMaterial = fillMaterial;
        UnityEngine.Object.DestroyImmediate(pearl.GetComponent<Collider>());

        GameObject pacingLight = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        pacingLight.name = "Follow-the-Light Pacing Orb";
        pacingLight.transform.SetParent(root.transform, false);
        pacingLight.GetComponent<Renderer>().sharedMaterial = faintMaterial;
        UnityEngine.Object.DestroyImmediate(pacingLight.GetComponent<Collider>());

        ParticleSystem fragments = CreateEnergyParticles(
            root.transform, "Gaussian-Like Teal and Gold Fragments", faintMaterial, 0.65f, 0.02f);
        ParticleSystem sparks = CreateEnergyParticles(
            root.transform, "Soft Warm Sparks", fillMaterial, 0.42f, 0.012f);
        ParticleSystem ripples = CreateEnergyParticles(
            root.transform, "Subtle Water Ripple Glimmers", faintMaterial, 0.80f, 0.05f);

        WayfinderCircularPalmGuide guide = root.AddComponent<WayfinderCircularPalmGuide>();
        guide.Configure(
            faint, fill, halo, tealRibbon, goldRibbon,
            pearl.transform, pacingLight.transform, fragments, sparks, ripples);
        root.SetActive(false);
        return guide;
    }

    private static LineRenderer CreateEnergyLine(
        Transform parent, string name, Material material, float width, Color color)
    {
        GameObject lineObject = new GameObject(name);
        lineObject.transform.SetParent(parent, false);
        LineRenderer line = lineObject.AddComponent<LineRenderer>();
        line.sharedMaterial = material;
        line.useWorldSpace = true;
        line.widthMultiplier = width;
        line.numCapVertices = 7;
        line.numCornerVertices = 5;
        line.startColor = color;
        line.endColor = new Color(color.r, color.g, color.b, color.a * 0.32f);
        line.positionCount = 0;
        return line;
    }

    private static ParticleSystem CreateEnergyParticles(
        Transform parent, string name, Material material, float lifetime, float size)
    {
        GameObject particleObject = new GameObject(name);
        particleObject.transform.SetParent(parent, false);
        ParticleSystem particles = particleObject.AddComponent<ParticleSystem>();
        ParticleSystem.MainModule main = particles.main;
        main.playOnAwake = false;
        main.loop = false;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startLifetime = lifetime;
        main.startSize = size;
        main.startSpeed = 0.025f;
        main.gravityModifier = -0.01f;
        ParticleSystem.EmissionModule emission = particles.emission;
        emission.enabled = false;
        ParticleSystem.NoiseModule noise = particles.noise;
        noise.enabled = true;
        noise.strength = 0.035f;
        noise.frequency = 0.45f;
        ParticleSystemRenderer renderer = particleObject.GetComponent<ParticleSystemRenderer>();
        renderer.sharedMaterial = material;
        particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        return particles;
    }

    private static WayfinderWorldRevealSlot CreateWorldLabsPlaceholderSlots(Material memoryMaterial, Material goldMaterial)
    {
        GameObject holder = new GameObject("WorldLabs Original-to-Memory Reveal Slot");
        holder.transform.position = new Vector3(0f, 1.55f, 3.05f);

        GameObject original = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        original.name = "WorldLabs_Original_World_Slot_PLACEHOLDER";
        original.transform.SetParent(holder.transform, false);
        original.GetComponent<Renderer>().sharedMaterial = memoryMaterial;
        UnityEngine.Object.DestroyImmediate(original.GetComponent<Collider>());

        GameObject memory = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        memory.name = "WorldLabs_Memory_World_Slot_PLACEHOLDER";
        memory.transform.SetParent(holder.transform, false);
        memory.GetComponent<Renderer>().sharedMaterial = goldMaterial;
        memory.transform.localScale = Vector3.one * 0.12f;
        UnityEngine.Object.DestroyImmediate(memory.GetComponent<Collider>());
        memory.SetActive(false);

        TextMesh label = CreateLegibleText(
            "WorldLabs Slot Label", new Vector3(0f, 0.82f, 0f), 48, 0.024f,
            TextAnchor.MiddleCenter, TextAlignment.Center, new Color(0.78f, 0.95f, 0.91f));
        label.text = "WORLDLABS ORIGINAL → MEMORY\nPLACEHOLDER SLOT";
        label.transform.SetParent(holder.transform, false);
        label.gameObject.SetActive(false);

        WayfinderWorldRevealSlot slot = holder.AddComponent<WayfinderWorldRevealSlot>();
        slot.Configure(original.transform, memory.transform);
        return slot;
    }

    private static WayfinderRewardMaterializationSlot CreateTripoPlaceholderSlot(Material material)
    {
        GameObject holder = new GameObject("Tripo Reward Slot Anchor");
        holder.transform.position = new Vector3(1.72f, 1.42f, 1.65f);
        GameObject visual = new GameObject("Tripo_Reward_Slot_PLACEHOLDER");
        visual.transform.SetParent(holder.transform, false);
        GameObject artifact = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        artifact.name = "Procedural Reward Stand-in";
        artifact.transform.SetParent(visual.transform, false);
        artifact.transform.localScale = new Vector3(0.62f, 0.92f, 0.62f);
        Renderer renderer = artifact.GetComponent<Renderer>();
        renderer.sharedMaterial = material;
        UnityEngine.Object.DestroyImmediate(artifact.GetComponent<Collider>());
        TextMesh label = CreateLegibleText(
            "Tripo Slot Label", new Vector3(0f, 0.72f, 0f), 46, 0.022f,
            TextAnchor.MiddleCenter, TextAlignment.Center, new Color(1f, 0.88f, 0.50f));
        label.text = "TRIPO REWARD SLOT\nPLACEHOLDER";
        label.transform.SetParent(visual.transform, false);
        WayfinderRewardMaterializationSlot slot = holder.AddComponent<WayfinderRewardMaterializationSlot>();
        slot.Configure(visual.transform, renderer, label);
        visual.SetActive(false);
        return slot;
    }

    private static WayfinderPalmDwellTarget[] CreateDwellTargets(Material reflectionMaterial, Material utilityMaterial)
    {
        return new[]
        {
            CreateDwellTarget("Reflection • Memory Seed", WayfinderDwellTargetKind.MemorySeed,
                new Vector3(-0.72f, 1.18f, -2.02f), "MEMORY\nSEED", reflectionMaterial, false),
            CreateDwellTarget("Reflection • Patience Stone", WayfinderDwellTargetKind.PatienceStone,
                new Vector3(0f, 1.18f, -2.02f), "PATIENCE\nSTONE", reflectionMaterial, false),
            CreateDwellTarget("Reflection • Still Water Lantern", WayfinderDwellTargetKind.StillWaterLantern,
                new Vector3(0.72f, 1.18f, -2.02f), "STILL WATER\nLANTERN", reflectionMaterial, false),
            CreateDwellTarget("Optional Palm Options", WayfinderDwellTargetKind.Options,
                new Vector3(1.30f, 1.28f, -2.05f), "OPTIONS", utilityMaterial, false),
            CreateDwellTarget("Technical View", WayfinderDwellTargetKind.ToggleJudgeHud,
                new Vector3(0.85f, 1.28f, -2.05f), "TECHNICAL\nVIEW", utilityMaterial, false),
            CreateDwellTarget("Start Over", WayfinderDwellTargetKind.DemoReset,
                new Vector3(1.30f, 0.88f, -2.05f), "START OVER", utilityMaterial, false)
        };
    }

    private static WayfinderPalmDwellTarget CreateDwellTarget(
        string name, WayfinderDwellTargetKind kind, Vector3 position, string copy,
        Material material, bool active)
    {
        GameObject target = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        target.name = name;
        target.transform.position = position;
        target.transform.localScale = Vector3.one * 0.15f;
        Renderer renderer = target.GetComponent<Renderer>();
        renderer.sharedMaterial = material;
        UnityEngine.Object.DestroyImmediate(target.GetComponent<Collider>());
        TextMesh label = CreateLegibleText(
            name + " Label", new Vector3(0f, 1.05f, 0f), 40, 0.15f,
            TextAnchor.MiddleCenter, TextAlignment.Center, Color.white);
        label.text = copy;
        label.transform.SetParent(target.transform, false);
        WayfinderPalmDwellTarget dwell = target.AddComponent<WayfinderPalmDwellTarget>();
        dwell.Configure(kind, renderer, label);
        target.SetActive(active);
        return dwell;
    }

    private static TextMesh CreateLegibleText(
        string name, Vector3 position, int fontSize, float characterSize,
        TextAnchor anchor, TextAlignment alignment, Color color)
    {
        GameObject textObject = new GameObject(name);
        textObject.transform.position = position;
        TextMesh text = textObject.AddComponent<TextMesh>();
        text.fontSize = fontSize;
        text.characterSize = characterSize;
        text.lineSpacing = 1.08f;
        text.anchor = anchor;
        text.alignment = alignment;
        text.color = color;
        return text;
    }

    private static void CreatePanelBehind(Transform target, Vector3 scale, Material material)
    {
        GameObject panel = GameObject.CreatePrimitive(PrimitiveType.Cube);
        panel.name = target.name + " Backplate";
        panel.transform.SetParent(target, false);
        panel.transform.localPosition = new Vector3(0f, 0f, 0.06f);
        panel.transform.localScale = scale;
        panel.GetComponent<Renderer>().sharedMaterial = material;
        UnityEngine.Object.DestroyImmediate(panel.GetComponent<Collider>());
        panel.transform.SetAsFirstSibling();
    }
}
