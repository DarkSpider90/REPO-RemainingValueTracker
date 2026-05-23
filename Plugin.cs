using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityObject = UnityEngine.Object;

namespace RemainingValueTracker;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    internal const string PluginGuid = "DarkSpider90.RemainingValueTracker";
    internal const string PluginName = "Remaining Value Tracker";
    internal const string PluginVersion = "0.1.1";

    private const float DefaultScanInterval = 0.5f;
    private static readonly Color MessageColor = new(0.8f, 1f, 0.35f);
    private static readonly Color MessageFlashColor = new(1f, 1f, 0.2f);
    private static readonly Color MessageShadowColor = new(0f, 0f, 0f, 0.75f);

    private static readonly AccessTools.FieldRef<ValuableObject, bool> DiscoveredRef =
        AccessTools.FieldRefAccess<ValuableObject, bool>("discovered");

    private static readonly AccessTools.FieldRef<ValuableObject, bool> DollarValueSetRef =
        AccessTools.FieldRefAccess<ValuableObject, bool>("dollarValueSet");

    private static readonly AccessTools.FieldRef<ValuableObject, float> DollarValueOriginalRef =
        AccessTools.FieldRefAccess<ValuableObject, float>("dollarValueOriginal");

    internal static ManualLogSource Log { get; private set; }

    private ConfigEntry<bool> _enableMod;
    private ConfigEntry<TrackingMode> _trackingMode;
    private ConfigEntry<float> _triggerPercent;
    private ConfigEntry<float> _scanInterval;
    private ConfigEntry<KeyboardShortcut> _revealHotkey;
    private ConfigEntry<bool> _showMessage;
    private ConfigEntry<float> _messageDuration;
    private ConfigEntry<string> _messageText;
    private ConfigEntry<bool> _debugLogs;

    private readonly List<ValuableObject> _roundValuables = new();
    private readonly Dictionary<int, float> _initialValues = new();

    private int _levelInstanceId;
    private bool _snapshotReady;
    private bool _revealed;
    private float _nextScanTime;
    private Coroutine _messageCoroutine;
    private GameObject _messageOverlay;
    private CanvasGroup _messageCanvasGroup;
    private TextMeshProUGUI _messageLabel;
    private TextMeshProUGUI _messageShadow;
    private bool _isQuitting;

    private void Awake()
    {
        Log = Logger;

        _enableMod = Config.Bind("General", "Enable Mod", true, "Master switch for Remaining Value Tracker.");
        _trackingMode = Config.Bind(
            "General",
            "Tracking Mode",
            TrackingMode.Value,
            "Value reveals remaining valuables after the configured percent of total value is discovered. Count uses item count instead.");
        _triggerPercent = Config.Bind(
            "General",
            "Trigger Percent",
            85f,
            new ConfigDescription(
                "Percent of the round's valuables that must be discovered before all remaining valuables are revealed.",
                new AcceptableValueRange<float>(1f, 100f)));
        _scanInterval = Config.Bind(
            "General",
            "Scan Interval",
            DefaultScanInterval,
            new ConfigDescription(
                "Seconds between tracker checks.",
                new AcceptableValueRange<float>(0.1f, 5f)));
        _revealHotkey = Config.Bind(
            "Controls",
            "Reveal Hotkey",
            new KeyboardShortcut(KeyCode.F10),
            "Press this key to reveal all remaining valuables immediately, as if the configured threshold was reached.");
        _showMessage = Config.Bind("UI", "Show Message", true, "Show a top-center message when remaining valuables are revealed.");
        _messageDuration = Config.Bind(
            "UI",
            "Message Duration",
            4f,
            new ConfigDescription(
                "Seconds to keep the reveal message visible.",
                new AcceptableValueRange<float>(0.5f, 15f)));
        _messageText = Config.Bind("UI", "Message Text", "All remaining valuables discovered", "Message shown when the tracker reveals the remaining valuables.");
        _debugLogs = Config.Bind("Diagnostics", "Debug Logs", false, "Print tracker snapshot and reveal details to the BepInEx log.");

        Log.LogInfo($"{PluginName} v{PluginVersion} loaded for R.E.P.O. v0.4.0.");
    }

    private void OnApplicationQuit()
    {
        _isQuitting = true;
        CleanupMessageOverlay();
    }

    private void OnDestroy()
    {
        _isQuitting = true;
        CleanupMessageOverlay();
    }

    private void Update()
    {
        if (_isQuitting)
        {
            return;
        }

        if (!_enableMod.Value)
        {
            return;
        }

        if (!LevelIsReady())
        {
            ResetRound();
            return;
        }

        int currentLevelId = LevelGenerator.Instance.GetInstanceID();
        if (_levelInstanceId != currentLevelId)
        {
            ResetRound(currentLevelId);
        }

        if (TryRevealByHotkey())
        {
            return;
        }

        if (_revealed || Time.time < _nextScanTime)
        {
            return;
        }

        _nextScanTime = Time.time + Mathf.Max(0.1f, _scanInterval.Value);

        if (!_snapshotReady && !TryBuildSnapshot())
        {
            return;
        }

        TrackerProgress progress = CalculateProgress();
        if (!progress.HasData)
        {
            return;
        }

        if (_debugLogs.Value)
        {
            Log.LogInfo($"Progress: {progress.Percent:0.##}% by {_trackingMode.Value} ({progress.Discovered:0.##}/{progress.Total:0.##}).");
        }

        float triggerPercent = Mathf.Clamp(_triggerPercent.Value, 1f, 100f);
        if (progress.Percent >= triggerPercent)
        {
            Log.LogInfo($"Reveal threshold reached: {progress.Percent:0.##}% >= {triggerPercent:0.##}%.");
            RevealRemaining();
        }
    }

    private bool TryRevealByHotkey()
    {
        if (_revealed || !_revealHotkey.Value.IsDown())
        {
            return false;
        }

        if (!_snapshotReady && !TryBuildSnapshot())
        {
            Log.LogWarning("Reveal hotkey was pressed, but valuables are not ready to scan yet.");
            return false;
        }

        Log.LogInfo("Reveal hotkey pressed.");
        RevealRemaining();
        return true;
    }

    private static bool LevelIsReady()
    {
        try
        {
            return RunManager.instance != null
                && LevelGenerator.Instance != null
                && LevelGenerator.Instance.Generated
                && ValuableDirector.instance != null
                && Map.Instance != null
                && SemiFunc.RunIsLevel();
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool TryBuildSnapshot()
    {
        ValuableObject[] found = UnityObject.FindObjectsOfType<ValuableObject>(false);
        if (found == null || found.Length == 0)
        {
            return false;
        }

        List<ValuableObject> valuables = found
            .Where(valuable => valuable != null && valuable.isActiveAndEnabled)
            .Distinct()
            .ToList();

        if (valuables.Count == 0 || valuables.Any(valuable => !DollarValueSetRef(valuable)))
        {
            return false;
        }

        _roundValuables.Clear();
        _roundValuables.AddRange(valuables);

        _initialValues.Clear();
        foreach (ValuableObject valuable in _roundValuables)
        {
            _initialValues[valuable.GetInstanceID()] = Mathf.Max(0f, DollarValueOriginalRef(valuable));
        }

        _snapshotReady = true;

        if (_debugLogs.Value)
        {
            float totalValue = _initialValues.Values.Sum();
            Log.LogInfo($"Snapshot ready: {_roundValuables.Count} valuables, total value {totalValue:0}.");
        }

        return true;
    }

    private TrackerProgress CalculateProgress()
    {
        if (_roundValuables.Count == 0)
        {
            return TrackerProgress.Empty;
        }

        float discovered = 0f;
        float total = 0f;

        foreach (ValuableObject valuable in _roundValuables)
        {
            if (valuable == null)
            {
                continue;
            }

            if (_trackingMode.Value == TrackingMode.Count)
            {
                total += 1f;
                if (DiscoveredRef(valuable))
                {
                    discovered += 1f;
                }

                continue;
            }

            if (!_initialValues.TryGetValue(valuable.GetInstanceID(), out float value))
            {
                continue;
            }

            total += value;
            if (DiscoveredRef(valuable))
            {
                discovered += value;
            }
        }

        if (total <= 0f)
        {
            return TrackerProgress.Empty;
        }

        return new TrackerProgress(discovered, total);
    }

    private void RevealRemaining()
    {
        _revealed = true;
        int revealedCount = 0;

        foreach (ValuableObject valuable in _roundValuables)
        {
            if (valuable == null || DiscoveredRef(valuable))
            {
                continue;
            }

            try
            {
                valuable.Discover(ValuableDiscoverGraphic.State.Discover);
                revealedCount++;
            }
            catch (Exception ex)
            {
                Log.LogWarning($"Could not reveal {valuable.name}: {ex.Message}");
            }
        }

        if (_showMessage.Value)
        {
            ShowRevealMessage();
        }

        Log.LogInfo($"Revealed {revealedCount} remaining valuables.");
    }

    private void ShowRevealMessage()
    {
        if (_isQuitting)
        {
            return;
        }

        if (_messageCoroutine != null)
        {
            StopCoroutine(_messageCoroutine);
        }

        _messageCoroutine = StartCoroutine(ShowRevealMessageRoutine());
    }

    private IEnumerator ShowRevealMessageRoutine()
    {
        EnsureMessageOverlay();

        string message = _messageText.Value;
        float duration = Mathf.Clamp(_messageDuration.Value, 0.5f, 15f);
        float endTime = Time.time + duration;
        float fadeIn = Mathf.Min(0.2f, duration * 0.25f);
        float fadeOut = Mathf.Min(0.35f, duration * 0.3f);

        SetOverlayText(message);
        _messageOverlay.SetActive(true);
        Log.LogInfo($"Showing reveal message for {duration:0.##}s.");

        while (Time.time < endTime)
        {
            float elapsed = duration - (endTime - Time.time);
            float remaining = endTime - Time.time;
            float alpha = 1f;

            if (fadeIn > 0f && elapsed < fadeIn)
            {
                alpha = Mathf.Clamp01(elapsed / fadeIn);
            }
            else if (fadeOut > 0f && remaining < fadeOut)
            {
                alpha = Mathf.Clamp01(remaining / fadeOut);
            }

            _messageCanvasGroup.alpha = alpha;

            yield return null;
        }

        _messageCanvasGroup.alpha = 0f;
        _messageOverlay.SetActive(false);
        _messageCoroutine = null;
    }

    private void EnsureMessageOverlay()
    {
        if (_messageOverlay != null)
        {
            return;
        }

        _messageOverlay = new GameObject("RemainingValueTracker Message Overlay");
        UnityObject.DontDestroyOnLoad(_messageOverlay);

        Canvas canvas = _messageOverlay.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 32760;

        CanvasScaler scaler = _messageOverlay.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        _messageCanvasGroup = _messageOverlay.AddComponent<CanvasGroup>();
        _messageCanvasGroup.blocksRaycasts = false;
        _messageCanvasGroup.interactable = false;

        _messageShadow = CreateOverlayText("Message Shadow", MessageShadowColor, new Vector2(2f, -2f));
        _messageLabel = CreateOverlayText("Message", MessageColor, Vector2.zero);

        _messageOverlay.SetActive(false);
    }

    private TextMeshProUGUI CreateOverlayText(string name, Color color, Vector2 offset)
    {
        GameObject textObject = new(name);
        textObject.transform.SetParent(_messageOverlay.transform, false);

        TextMeshProUGUI text = textObject.AddComponent<TextMeshProUGUI>();
        text.raycastTarget = false;
        text.alignment = TextAlignmentOptions.Center;
        text.fontStyle = FontStyles.Bold;
        text.color = color;
        text.fontSize = 36f;
        text.enableAutoSizing = true;
        text.fontSizeMin = 18f;
        text.fontSizeMax = 38f;
        text.enableWordWrapping = true;

        TMP_FontAsset font = FindExistingFont();
        if (font != null)
        {
            text.font = font;
        }

        RectTransform rect = text.rectTransform;
        rect.anchorMin = new Vector2(0.5f, 1f);
        rect.anchorMax = new Vector2(0.5f, 1f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.anchoredPosition = new Vector2(offset.x, -95f + offset.y);
        rect.sizeDelta = new Vector2(1200f, 96f);

        return text;
    }

    private static TMP_FontAsset FindExistingFont()
    {
        TextMeshProUGUI existingText = UnityObject.FindObjectOfType<TextMeshProUGUI>();
        return existingText != null ? existingText.font : null;
    }

    private void SetOverlayText(string message)
    {
        _messageLabel.text = message;
        _messageShadow.text = message;
    }

    private void ResetRound(int levelInstanceId = 0)
    {
        StopMessageCoroutine();

        _levelInstanceId = levelInstanceId;
        _snapshotReady = false;
        _revealed = false;
        _nextScanTime = 0f;
        _roundValuables.Clear();
        _initialValues.Clear();
    }

    private void StopMessageCoroutine()
    {
        if (_messageCoroutine == null)
        {
            return;
        }

        StopCoroutine(_messageCoroutine);
        _messageCoroutine = null;
    }

    private void CleanupMessageOverlay()
    {
        StopMessageCoroutine();

        if (_messageOverlay == null)
        {
            return;
        }

        UnityObject.Destroy(_messageOverlay);
        _messageOverlay = null;
        _messageCanvasGroup = null;
        _messageLabel = null;
        _messageShadow = null;
    }

    private enum TrackingMode
    {
        Value,
        Count
    }

    private readonly struct TrackerProgress
    {
        internal static readonly TrackerProgress Empty = new(0f, 0f);

        internal TrackerProgress(float discovered, float total)
        {
            Discovered = discovered;
            Total = total;
        }

        internal float Discovered { get; }

        internal float Total { get; }

        internal float Percent => Total <= 0f ? 0f : Discovered / Total * 100f;

        internal bool HasData => Total > 0f;
    }
}
