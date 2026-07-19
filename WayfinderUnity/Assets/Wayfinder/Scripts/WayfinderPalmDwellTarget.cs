using UnityEngine;

public sealed class WayfinderPalmDwellTarget : MonoBehaviour
{
    [SerializeField] private WayfinderDwellTargetKind kind;
    [SerializeField] private Renderer targetRenderer;
    [SerializeField] private TextMesh label;

    private Vector3 baseScale;
    private Color baseColor = new Color(0.35f, 0.82f, 0.72f, 0.8f);

    public WayfinderDwellTargetKind Kind => kind;
    public Vector3 Position => transform.position;

    private void Awake()
    {
        baseScale = transform.localScale;
        if (targetRenderer != null) baseColor = targetRenderer.material.color;
    }

    public void SetProgress(float progress01, bool available)
    {
        gameObject.SetActive(available);
        if (!available) return;
        float progress = Mathf.Clamp01(progress01);
        transform.localScale = baseScale * Mathf.Lerp(1f, 1.28f, progress);
        if (targetRenderer != null)
        {
            targetRenderer.material.color = Color.Lerp(baseColor, new Color(1f, 0.84f, 0.35f, 1f), progress);
        }
        if (label != null)
        {
            label.color = Color.Lerp(new Color(0.78f, 0.92f, 0.88f), Color.white, progress);
        }
    }

    public void Configure(WayfinderDwellTargetKind targetKind, Renderer renderer, TextMesh targetLabel)
    {
        kind = targetKind;
        targetRenderer = renderer;
        label = targetLabel;
    }
}
