using GaussianSplatting.Runtime;
using UnityEngine;

/// <summary>
/// Crossfades two prebuilt memory worlds after reflection. No world or content
/// is generated at runtime; the spark veil hides the renderer handoff.
/// </summary>
public sealed class WayfinderMemoryWorldTransition : MonoBehaviour
{
    [SerializeField] private GaussianSplatRenderer bambooWorld;
    [SerializeField] private GaussianSplatRenderer celestialWorld;
    [SerializeField] private ParticleSystem sparkVeil;
    [SerializeField, Min(0.2f)] private float transitionSeconds = 2.4f;

    private float progress;
    private bool transitioning;

    public bool IsCelestial => celestialWorld != null && celestialWorld.m_RenderEnabled && progress >= 1f;

    public void Configure(
        GaussianSplatRenderer bamboo,
        GaussianSplatRenderer celestial,
        ParticleSystem sparks)
    {
        bambooWorld = bamboo;
        celestialWorld = celestial;
        sparkVeil = sparks;
    }

    public void BeginCelestialTransition()
    {
        if (transitioning || IsCelestial || celestialWorld == null) return;
        progress = 0f;
        transitioning = true;
        celestialWorld.gameObject.SetActive(true);
        celestialWorld.m_RenderEnabled = true;
        celestialWorld.m_OpacityScale = 0.05f;
        if (sparkVeil != null)
        {
            sparkVeil.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            sparkVeil.Play(true);
        }
    }

    public void ResetTransition()
    {
        progress = 0f;
        transitioning = false;
        if (bambooWorld != null)
        {
            bambooWorld.gameObject.SetActive(true);
            bambooWorld.m_OpacityScale = 0.05f;
        }
        if (celestialWorld != null)
        {
            celestialWorld.m_RenderEnabled = false;
            celestialWorld.m_OpacityScale = 0.05f;
            celestialWorld.gameObject.SetActive(false);
        }
    }

    private void Update()
    {
        if (!transitioning) return;
        progress = Mathf.MoveTowards(progress, 1f,
            Time.unscaledDeltaTime / Mathf.Max(0.2f, transitionSeconds));
        float eased = progress * progress * (3f - 2f * progress);
        if (bambooWorld != null) bambooWorld.m_OpacityScale = Mathf.Lerp(1f, 0.05f, eased);
        if (celestialWorld != null) celestialWorld.m_OpacityScale = Mathf.Lerp(0.05f, 1f, eased);
        if (progress < 1f) return;
        transitioning = false;
        if (bambooWorld != null)
        {
            bambooWorld.m_RenderEnabled = false;
            bambooWorld.gameObject.SetActive(false);
        }
    }
}
