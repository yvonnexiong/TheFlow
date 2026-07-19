using UnityEngine;

public sealed class WayfinderWorldRevealSlot : MonoBehaviour, IWayfinderWorldSlot
{
    [SerializeField] private Transform originalWorldLabsSlot;
    [SerializeField] private Transform memoryWorldLabsSlot;
    [SerializeField, Min(0.1f)] private float revealSeconds = 1.8f;

    private float reveal01;
    public bool IsOpen { get; private set; }

    private void Awake() => ApplyReveal(0f);

    private void Update()
    {
        float target = IsOpen ? 1f : 0f;
        reveal01 = Mathf.MoveTowards(reveal01, target, Time.deltaTime / Mathf.Max(0.1f, revealSeconds));
        ApplyReveal(reveal01);
    }

    public void OpenPlaceholder() => IsOpen = true;

    public void ResetPlaceholder()
    {
        IsOpen = false;
        reveal01 = 0f;
        ApplyReveal(0f);
    }

    public void Configure(Transform originalSlot, Transform memorySlot)
    {
        originalWorldLabsSlot = originalSlot;
        memoryWorldLabsSlot = memorySlot;
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
            memoryWorldLabsSlot.gameObject.SetActive(eased > 0.005f);
            memoryWorldLabsSlot.localScale = Vector3.one * Mathf.Lerp(0.12f, 1f, eased);
            memoryWorldLabsSlot.localRotation = Quaternion.Euler(0f, eased * 18f, 0f);
        }
    }
}
