using UnityEngine;

public sealed class WayfinderRewardMaterializationSlot : MonoBehaviour, IWayfinderRewardSlot
{
    [SerializeField] private Transform tripoRewardSlot;
    [SerializeField] private Renderer rewardRenderer;
    [SerializeField] private TextMesh rewardLabel;

    private float materialize01;
    private bool granted;

    private void Awake() => ApplyMaterialization(0f);

    private void Update()
    {
        materialize01 = Mathf.MoveTowards(materialize01, granted ? 1f : 0f, Time.deltaTime * 1.2f);
        ApplyMaterialization(materialize01);
    }

    public void GrantPlaceholder(string artifactId, WayfinderRewardVariant variant)
    {
        granted = true;
        materialize01 = 0f;
        if (rewardLabel != null)
        {
            rewardLabel.text = DisplayName(artifactId) + "\n" + variant.ToString().ToUpperInvariant();
        }
        if (rewardRenderer != null) rewardRenderer.material.color = VariantColor(variant);
    }

    public void ResetPlaceholder()
    {
        granted = false;
        materialize01 = 0f;
        if (rewardLabel != null) rewardLabel.text = "TRIPO REWARD SLOT\nPLACEHOLDER";
        ApplyMaterialization(0f);
    }

    public void Configure(Transform rewardSlot, Renderer renderer, TextMesh label)
    {
        tripoRewardSlot = rewardSlot;
        rewardRenderer = renderer;
        rewardLabel = label;
    }

    private void ApplyMaterialization(float amount)
    {
        if (tripoRewardSlot == null) return;
        tripoRewardSlot.gameObject.SetActive(amount > 0.001f);
        float eased = 1f - Mathf.Pow(1f - Mathf.Clamp01(amount), 3f);
        tripoRewardSlot.localScale = Vector3.one * eased;
        tripoRewardSlot.localRotation = Quaternion.Euler(0f, amount * 120f, 0f);
    }

    private static Color VariantColor(WayfinderRewardVariant variant)
    {
        switch (variant)
        {
            case WayfinderRewardVariant.Luminous: return new Color(1f, 0.82f, 0.30f);
            case WayfinderRewardVariant.Steady: return new Color(0.42f, 0.94f, 0.78f);
            default: return new Color(0.58f, 0.72f, 0.68f);
        }
    }

    private static string DisplayName(string artifactId)
    {
        return string.IsNullOrEmpty(artifactId)
            ? "MEMORY"
            : artifactId.Replace('_', ' ').ToUpperInvariant();
    }
}
