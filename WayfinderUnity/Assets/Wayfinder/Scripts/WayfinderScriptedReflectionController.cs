using UnityEngine;

public enum WayfinderReflectionStage
{
    Waiting,
    PromptOne,
    PromptTwo,
    Closing,
    Quiet
}

/// <summary>
/// Small deterministic conversation for the offline release. It deliberately
/// uses hand-touch choices so the ending never depends on networking, speech
/// recognition, an LLM, controllers, triggers, grips, or dwell timers.
/// </summary>
public sealed class WayfinderScriptedReflectionController : MonoBehaviour
{
    [SerializeField] private WayfinderWorldRevealSlot worldSlot;
    [SerializeField] private MonoBehaviour palmPoseSource;
    [SerializeField] private GameObject presentationRoot;
    [SerializeField] private TextMesh worldText;
    [SerializeField] private TextMesh journalText;
    [SerializeField] private Transform[] choiceButtons;
    [SerializeField] private TextMesh[] choiceLabels;
    [SerializeField] private Transform quietButton;
    [SerializeField] private TextMesh quietButtonLabel;
    [SerializeField] private WayfinderMemoryWorldTransition finalWorldTransition;
    [SerializeField, Min(0.05f)] private float touchRadiusMeters = 0.13f;
    [SerializeField, Min(0f)] private float arrivalDelaySeconds = 3.4f;
    [SerializeField, Min(1f)] private float closingSeconds = 4f;

    private IWayfinderPalmPoseSource palms;
    private float stageSeconds;
    private bool touchArmed = true;
    private int firstChoice = -1;
    private bool completionPending;
    private WayfinderReflectionChoice completionChoice;

    public WayfinderReflectionStage Stage { get; private set; } = WayfinderReflectionStage.Waiting;
    public bool IsVisible => presentationRoot != null && presentationRoot.activeSelf;

    private void Awake()
    {
        palms = palmPoseSource as IWayfinderPalmPoseSource;
        ResetReflection();
    }

    private void Update()
    {
        if (worldSlot == null || !worldSlot.IsOpen)
        {
            if (Stage != WayfinderReflectionStage.Waiting || IsVisible) ResetReflection();
            return;
        }

        stageSeconds += Time.unscaledDeltaTime;
        if (Stage == WayfinderReflectionStage.Waiting)
        {
            if (stageSeconds >= arrivalDelaySeconds) ShowPromptOne();
            return;
        }
        if (Stage == WayfinderReflectionStage.Closing)
        {
            if (stageSeconds >= closingSeconds) ShowQuiet();
            return;
        }
        if (Stage == WayfinderReflectionStage.Quiet) return;

        UpdateTouchInput();
    }

    public void Configure(
        WayfinderWorldRevealSlot slot,
        MonoBehaviour poseSource,
        GameObject root,
        TextMesh prompt,
        TextMesh journal,
        Transform[] buttons,
        TextMesh[] labels,
        Transform skipButton,
        TextMesh skipLabel,
        WayfinderMemoryWorldTransition worldTransition = null)
    {
        worldSlot = slot;
        palmPoseSource = poseSource;
        presentationRoot = root;
        worldText = prompt;
        journalText = journal;
        choiceButtons = buttons;
        choiceLabels = labels;
        quietButton = skipButton;
        quietButtonLabel = skipLabel;
        finalWorldTransition = worldTransition;
        palms = poseSource as IWayfinderPalmPoseSource;
    }

    public void ResetReflection()
    {
        Stage = WayfinderReflectionStage.Waiting;
        stageSeconds = 0f;
        touchArmed = true;
        firstChoice = -1;
        completionPending = false;
        completionChoice = WayfinderReflectionChoice.MemorySeed;
        if (finalWorldTransition != null) finalWorldTransition.ResetTransition();
        if (presentationRoot != null) presentationRoot.SetActive(false);
    }

    public bool TryConsumeCompletion(out WayfinderReflectionChoice choice)
    {
        choice = completionChoice;
        if (!completionPending) return false;
        completionPending = false;
        return true;
    }

    public void DemoSelectOpeningAnswer(int choice)
    {
        if (Stage == WayfinderReflectionStage.PromptOne) ShowPromptTwo(choice);
    }

    public void DemoSelectSecondAnswer(int choice)
    {
        if (Stage == WayfinderReflectionStage.PromptTwo) ShowClosing(choice);
    }

    public static string OpeningPrompt =>
        "THE WORLD ASKS\n\nWHAT FEELS MOST PRESENT\nIN YOUR LIFE TODAY?";

    public static string SecondPrompt(int choice)
    {
        switch (choice)
        {
            case 0: return "YOU HAVE BEEN CARRYING A LOT.\n\nWHERE DID PATIENCE\nMEET YOU TODAY?";
            case 1: return "HOLD THAT WARMTH GENTLY.\n\nWHERE DID PATIENCE\nMEET YOU TODAY?";
            default: return "NOT KNOWING IS ALLOWED.\n\nWHERE DID PATIENCE\nMEET YOU TODAY?";
        }
    }

    private void ShowPromptOne()
    {
        Stage = WayfinderReflectionStage.PromptOne;
        stageSeconds = 0f;
        if (presentationRoot != null) presentationRoot.SetActive(true);
        if (worldText != null) worldText.text = OpeningPrompt;
        if (journalText != null) journalText.text = "TODAY'S REFLECTION\n\nTOUCH THE WORDS THAT FEEL CLOSEST.";
        SetChoices(new[] { "A LOT", "HOPEFUL", "I'M NOT SURE" }, "SKIP • JUST SIT");
    }

    private void ShowPromptTwo(int choice)
    {
        firstChoice = Mathf.Clamp(choice, 0, 2);
        Stage = WayfinderReflectionStage.PromptTwo;
        stageSeconds = 0f;
        if (worldText != null) worldText.text = SecondPrompt(firstChoice);
        if (journalText != null)
            journalText.text = "TODAY'S REFLECTION\n\nLIFE FELT " + FirstChoiceJournal(firstChoice) + ".";
        SetChoices(new[] { "I WAITED", "I LISTENED", "I KEPT GOING" }, "I'M DONE");
    }

    private void ShowClosing(int choice)
    {
        completionChoice = ChoiceForSecondAnswer(choice);
        completionPending = true;
        Stage = WayfinderReflectionStage.Closing;
        stageSeconds = 0f;
        if (worldText != null)
            worldText.text = "THE WORLD REMEMBERS.\n\nYOUR ATTENTION IS A PLACE.\nRETURN TO IT.";
        if (journalText != null)
            journalText.text = "TODAY'S REFLECTION\n\nLIFE FELT " + FirstChoiceJournal(firstChoice) +
                               ".\n\nPATIENCE APPEARED WHEN\n" + SecondChoiceJournal(choice) + ".";
        SetButtonsVisible(false);
        if (finalWorldTransition != null) finalWorldTransition.BeginCelestialTransition();
    }

    private void ShowQuiet()
    {
        Stage = WayfinderReflectionStage.Quiet;
        stageSeconds = 0f;
        if (worldText != null) worldText.text = "STAY AS LONG AS YOU NEED.";
        SetButtonsVisible(false);
    }

    private void SkipToQuiet()
    {
        completionChoice = WayfinderReflectionChoice.MemorySeed;
        completionPending = true;
        Stage = WayfinderReflectionStage.Quiet;
        stageSeconds = 0f;
        if (worldText != null) worldText.text = "NO WORDS ARE REQUIRED.\n\nSTAY AS LONG AS YOU NEED.";
        if (journalText != null) journalText.text = "TODAY'S REFLECTION\n\nI CHOSE A MOMENT OF QUIET.";
        SetButtonsVisible(false);
    }

    private void UpdateTouchInput()
    {
        Pose leftPose = default;
        Pose rightPose = default;
        bool left = palms != null && palms.TryGetLeftPalm(out leftPose);
        bool right = palms != null && palms.TryGetRightPalm(out rightPose);

        float nearest = float.PositiveInfinity;
        int selected = -1;
        if (choiceButtons != null)
        {
            for (int index = 0; index < choiceButtons.Length; index++)
            {
                Transform button = choiceButtons[index];
                if (button == null || !button.gameObject.activeInHierarchy) continue;
                float distance = NearestDistance(left, leftPose.position, right, rightPose.position, button.position);
                if (distance < nearest) { nearest = distance; selected = index; }
            }
        }
        float quietDistance = quietButton != null && quietButton.gameObject.activeInHierarchy
            ? NearestDistance(left, leftPose.position, right, rightPose.position, quietButton.position)
            : float.PositiveInfinity;

        float radius = Mathf.Max(0.05f, touchRadiusMeters);
        if (!touchArmed)
        {
            if (Mathf.Min(nearest, quietDistance) > radius * 1.6f) touchArmed = true;
            return;
        }
        if (quietDistance <= radius)
        {
            touchArmed = false;
            SkipToQuiet();
            return;
        }
        if (selected < 0 || nearest > radius) return;

        touchArmed = false;
        if (Stage == WayfinderReflectionStage.PromptOne) ShowPromptTwo(selected);
        else if (Stage == WayfinderReflectionStage.PromptTwo) ShowClosing(selected);
    }

    private void SetChoices(string[] labels, string quietLabel)
    {
        SetButtonsVisible(true);
        for (int index = 0; choiceLabels != null && index < choiceLabels.Length; index++)
            if (choiceLabels[index] != null)
                choiceLabels[index].text = index < labels.Length ? labels[index] : string.Empty;
        if (quietButtonLabel != null) quietButtonLabel.text = quietLabel;
    }

    private void SetButtonsVisible(bool visible)
    {
        if (choiceButtons != null)
            foreach (Transform button in choiceButtons)
                if (button != null) button.gameObject.SetActive(visible);
        if (quietButton != null) quietButton.gameObject.SetActive(visible);
    }

    private static float NearestDistance(
        bool left, Vector3 leftPosition, bool right, Vector3 rightPosition, Vector3 target)
    {
        return Mathf.Min(
            left ? Vector3.Distance(leftPosition, target) : float.PositiveInfinity,
            right ? Vector3.Distance(rightPosition, target) : float.PositiveInfinity);
    }

    private static string FirstChoiceJournal(int choice)
    {
        return choice == 0 ? "FULL" : choice == 1 ? "HOPEFUL" : "UNCERTAIN";
    }

    private static string SecondChoiceJournal(int choice)
    {
        return choice == 0 ? "I WAITED" : choice == 1 ? "I LISTENED" : "I KEPT GOING";
    }

    public static WayfinderReflectionChoice ChoiceForSecondAnswer(int choice)
    {
        if (choice == 0) return WayfinderReflectionChoice.PatienceStone;
        if (choice == 1) return WayfinderReflectionChoice.MemorySeed;
        return WayfinderReflectionChoice.StillWaterLantern;
    }
}
