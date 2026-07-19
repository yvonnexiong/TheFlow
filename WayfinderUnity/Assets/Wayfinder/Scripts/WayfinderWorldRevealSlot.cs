using UnityEngine;
using GaussianSplatting.Runtime;

public sealed class WayfinderWorldRevealSlot : MonoBehaviour, IWayfinderWorldSlot
{
    [SerializeField] private Transform originalWorldLabsSlot;
    [SerializeField] private Transform memoryWorldLabsSlot;
    [SerializeField] private GaussianSplatRenderer rewardSplatRenderer;
    [SerializeField] private ParticleSystem transitionSparks;
    [SerializeField] private Light transitionLight;
    [SerializeField, Min(0.1f)] private float revealSeconds = 1.8f;

    private float reveal01;
    private bool prepared;
    public bool IsOpen { get; private set; }
    public float Reveal01 => reveal01;

    private void Awake()
    {
        if (memoryWorldLabsSlot != null) memoryWorldLabsSlot.gameObject.SetActive(false);
        ApplyReveal(0f);
    }

    private void Update()
    {
        float target = IsOpen ? 1f : 0f;
        reveal01 = Mathf.MoveTowards(reveal01, target, Time.deltaTime / Mathf.Max(0.1f, revealSeconds));
        ApplyReveal(reveal01);
    }

    public void Prepare()
    {
        if (prepared) return;
        prepared = true;
        if (memoryWorldLabsSlot != null) memoryWorldLabsSlot.gameObject.SetActive(true);
        if (rewardSplatRenderer != null)
        {
            rewardSplatRenderer.m_OpacityScale = 0.05f;
            rewardSplatRenderer.m_RenderEnabled = false;
        }
    }

    public void OpenPlaceholder()
    {
        Prepare();
        IsOpen = true;
        if (rewardSplatRenderer != null) rewardSplatRenderer.m_RenderEnabled = true;
        if (transitionSparks != null)
        {
            transitionSparks.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            transitionSparks.Play(true);
        }
    }

    public void ResetPlaceholder()
    {
        IsOpen = false;
        reveal01 = 0f;
        prepared = false;
        if (rewardSplatRenderer != null)
        {
            rewardSplatRenderer.m_RenderEnabled = false;
            rewardSplatRenderer.m_OpacityScale = 0.05f;
        }
        ApplyReveal(0f);
        if (memoryWorldLabsSlot != null) memoryWorldLabsSlot.gameObject.SetActive(false);
        if (transitionSparks != null)
            transitionSparks.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
    }

    public void Configure(Transform originalSlot, Transform memorySlot)
    {
        originalWorldLabsSlot = originalSlot;
        memoryWorldLabsSlot = memorySlot;
    }

    public void ConfigureReward(
        GaussianSplatRenderer splatRenderer, ParticleSystem sparks, Light revealLight)
    {
        rewardSplatRenderer = splatRenderer;
        transitionSparks = sparks;
        transitionLight = revealLight;
    }

    private void ApplyReveal(float amount)
    {
        float eased = amount * amount * (3f - 2f * amount);
        if (originalWorldLabsSlot != null)
        {
            originalWorldLabsSlot.localScale = Vector3.one * Mathf.Lerp(1f, 0.12f, eased);
            originalWorldLabsSlot.gameObject.SetActive(eased < 0.995f);
        }
        if (memoryWorldLabsSlot != null)
        {
            if (prepared) memoryWorldLabsSlot.gameObject.SetActive(true);
            memoryWorldLabsSlot.localScale = Vector3.one;
        }
        if (rewardSplatRenderer != null)
        {
            rewardSplatRenderer.m_OpacityScale = Mathf.Lerp(0.05f, 1f, eased);
        }
        if (transitionLight != null)
        {
            float veil = Mathf.Sin(eased * Mathf.PI);
            transitionLight.intensity = Mathf.Lerp(0.15f, 5.5f, veil);
        }
    }
}
