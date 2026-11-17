using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Il2CppAssets.Scripts.Unity.Menu;
using Il2CppAssets.Scripts.Unity.UI_New.Popups;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace AgentBridge;

// Generic menu navigation: list every visible, interactable Button/Toggle on screen (any menu, popup or
// overlay) and press one through its normal UI handler. respond_ui only knows a fixed set of blockers;
// this covers every other screen (mode intro cards, map select, Knowledge, Mod Settings, rewards...).
public sealed partial class AgentBridgeMod
{
    private const int UiSnapshotMaxEntries = 400;

    // Never pressed through ui_click: real-money storefronts and profile-mutating research controls.
    private static readonly string[] UiBlockedMenuFragments = { "Store", "Iap", "IAP", "PurchaseMenu", "Checkout" };
    private static readonly string[] UiBlockedTextFragments = { "Provision research", "Provision Research" };
    // Entry buttons for the store, leaderboard events (the user's alt-account rules) and quitting the game.
    private static readonly string[] UiBlockedPathFragments =
        { "MainMenu/Store/", "OdysseyButton", "ContestedTerritoryButton", "RaceButton", "MainMenu/ExitButton/" };

    private sealed record UiControl(int Id, string Kind, Selectable Selectable, string Path, string? Text,
        string? Menu, bool InPopup, bool OnScreen, float X, float Y, bool IsOn);

    private static string UiPath(Transform t)
    {
        var names = new List<string>(12);
        for (var cur = t; cur != null && names.Count < 12; cur = cur.parent) names.Add(cur.name);
        names.Reverse();
        return string.Join("/", names);
    }

    private static string? UiControlText(GameObject go)
    {
        var parts = new List<string>(4);
        foreach (var tmp in go.GetComponentsInChildren<TMP_Text>(false))
        {
            string? s = tmp?.text?.Trim();
            if (!string.IsNullOrEmpty(s) && !parts.Contains(s)) parts.Add(s);
            if (parts.Count >= 4) break;
        }
        if (parts.Count == 0)
            foreach (var txt in go.GetComponentsInChildren<Text>(false))
            {
                string? s = txt?.text?.Trim();
                if (!string.IsNullOrEmpty(s) && !parts.Contains(s)) parts.Add(s);
                if (parts.Count >= 4) break;
            }
        return parts.Count == 0 ? null : UiText(string.Join(" | ", parts));
    }

    private static List<UiControl> CollectUiControls()
    {
        var list = new List<UiControl>();
        var popupRoot = PopupScreen.instance != null && PopupScreen.instance.IsPopupActiveOrLoading()
            ? PopupScreen.instance.transform : null;
        void Add(Selectable sel, string kind, bool isOn)
        {
            if (sel == null || !sel.isActiveAndEnabled || !sel.IsInteractable()) return;
            var go = sel.gameObject;
            if (go == null || !go.activeInHierarchy || !go.scene.IsValid()) return;
            var rt = go.GetComponent<RectTransform>();
            var canvas = go.GetComponentInParent<Canvas>();
            var root = canvas != null ? canvas.rootCanvas : null;
            Camera? cam = root != null && root.renderMode != RenderMode.ScreenSpaceOverlay ? root.worldCamera : null;
            Vector2 sp = rt != null ? RectTransformUtility.WorldToScreenPoint(cam, rt.position) : Vector2.zero;
            bool onScreen = rt != null && sp.x >= 0 && sp.y >= 0 && sp.x <= Screen.width && sp.y <= Screen.height;
            var menu = go.GetComponentInParent<GameMenu>();
            bool inPopup = popupRoot != null && go.transform.IsChildOf(popupRoot);
            list.Add(new UiControl(go.GetInstanceID(), kind, sel, UiPath(go.transform), UiControlText(go),
                menu != null ? menu.GetIl2CppType().Name : null, inPopup, onScreen,
                Screen.width > 0 ? sp.x / Screen.width : 0f, Screen.height > 0 ? 1f - sp.y / Screen.height : 0f, isOn));
        }
        foreach (var b in UnityEngine.Resources.FindObjectsOfTypeAll<Button>()) Add(b, "button", false);
        foreach (var t in UnityEngine.Resources.FindObjectsOfTypeAll<Toggle>()) Add(t, "toggle", t.isOn);
        return list;
    }

    private static string? UiBlockReason(UiControl c)
    {
        if (c.Menu != null && UiBlockedMenuFragments.Any(f => c.Menu.Contains(f, StringComparison.Ordinal)))
            return $"Controls on {c.Menu} are not pressed through the bridge (real-money store).";
        if (c.Text != null && UiBlockedTextFragments.Any(f => c.Text.Contains(f, StringComparison.Ordinal)))
            return "Profile-mutating research controls are never pressed through the bridge.";
        if (UiBlockedPathFragments.Any(f => c.Path.Contains(f, StringComparison.Ordinal)))
            return "Store, leaderboard-event (Race, Odyssey, Contested Territory) and Exit buttons are not pressed through the bridge.";
        return null;
    }

    private static object UiControlDto(UiControl c, bool popupActive) => new
    {
        c.Id, c.Kind, c.Text, c.Path, c.Menu, c.InPopup,
        BehindPopup = popupActive && !c.InPopup,
        c.OnScreen,
        X = Math.Round(c.X, 4), Y = Math.Round(c.Y, 4),
        IsOn = c.Kind == "toggle" ? c.IsOn : (bool?)null,
        Blocked = UiBlockReason(c)
    };

    private static BridgeResultV1 HandleUiSnapshot(BridgeRequestV1 request)
    {
        string textFilter = ReadString(request.Payload, "textContains", "").Trim();
        string pathFilter = ReadString(request.Payload, "pathContains", "").Trim();
        string menuFilter = ReadString(request.Payload, "menu", "").Trim();
        int limit = Math.Clamp(ReadInt(request.Payload, "limit", 150), 1, UiSnapshotMaxEntries);
        bool includeOffscreen = request.Payload.ValueKind == JsonValueKind.Object &&
            request.Payload.TryGetProperty("includeOffscreen", out var off) && off.ValueKind == JsonValueKind.True;

        UpdateUiState(false);
        var controls = CollectUiControls();
        bool popupActive = controls.Any(c => c.InPopup);
        IEnumerable<UiControl> q = controls;
        if (!includeOffscreen) q = q.Where(c => c.OnScreen);
        if (textFilter.Length > 0) q = q.Where(c => c.Text?.Contains(textFilter, StringComparison.OrdinalIgnoreCase) == true);
        if (pathFilter.Length > 0) q = q.Where(c => c.Path.Contains(pathFilter, StringComparison.OrdinalIgnoreCase));
        if (menuFilter.Length > 0) q = q.Where(c => string.Equals(c.Menu, menuFilter, StringComparison.OrdinalIgnoreCase));
        // Popup controls first, then top-to-bottom, left-to-right on screen.
        var ordered = q.OrderByDescending(c => c.InPopup).ThenBy(c => Math.Round(c.Y, 2)).ThenBy(c => c.X).ToList();

        var current = MenuManager.instance?.GetCurrentMenu();
        Popup? popup = null;
        var popups = PopupScreen.instance;
        if (popups != null) popups.TryGetActivePopup(out popup);
        return SuccessResult(request, new
        {
            CurrentMenu = current != null ? current.GetIl2CppType().Name : null,
            InGame = IsActiveGame(),
            Popup = popup == null ? null : new { Type = popup.GetIl2CppType().Name, Title = UiText(popup.title?.text), Body = UiText(popup.body?.text) },
            Ui = uiState,
            ScreenWidth = Screen.width,
            ScreenHeight = Screen.height,
            Total = ordered.Count,
            Returned = Math.Min(limit, ordered.Count),
            Controls = ordered.Take(limit).Select(c => UiControlDto(c, popupActive)).ToArray()
        });
    }

    private static BridgeResultV1 HandleUiClick(BridgeRequestV1 request)
    {
        int id = ReadInt(request.Payload, "id", 0);
        string path = ReadString(request.Payload, "path", "").Trim();
        string text = ReadString(request.Payload, "text", "").Trim();
        string menu = ReadString(request.Payload, "menu", "").Trim();
        string mode = ReadString(request.Payload, "mode", "onClick").Trim();
        bool allowBehindPopup = request.Payload.ValueKind == JsonValueKind.Object &&
            request.Payload.TryGetProperty("allowBehindPopup", out var abp) && abp.ValueKind == JsonValueKind.True;
        if (id == 0 && path.Length == 0 && text.Length == 0)
            return ErrorResult(request, "INVALID_ARGUMENT", "Give id (from ui_snapshot), path, or text.", false);
        if (mode is not ("onClick" or "pointer"))
            return ErrorResult(request, "INVALID_ARGUMENT", "mode must be 'onClick' or 'pointer'.", false);

        var controls = CollectUiControls();
        bool popupActive = controls.Any(c => c.InPopup);
        IEnumerable<UiControl> q = controls;
        if (menu.Length > 0) q = q.Where(c => string.Equals(c.Menu, menu, StringComparison.OrdinalIgnoreCase));
        List<UiControl> matches;
        if (id != 0) matches = q.Where(c => c.Id == id).ToList();
        else if (path.Length > 0) matches = q.Where(c => string.Equals(c.Path, path, StringComparison.Ordinal)).ToList();
        else
        {
            var exact = q.Where(c => c.OnScreen && c.Text != null && string.Equals(c.Text, text, StringComparison.OrdinalIgnoreCase)).ToList();
            matches = exact.Count > 0 ? exact
                : q.Where(c => c.OnScreen && c.Text?.Contains(text, StringComparison.OrdinalIgnoreCase) == true).ToList();
        }
        if (popupActive && !allowBehindPopup && matches.Any(c => c.InPopup)) matches = matches.Where(c => c.InPopup).ToList();
        if (matches.Count == 0)
            return ErrorResult(request, "UI_CONTROL_NOT_FOUND", "No visible, interactable control matches. Call ui_snapshot and use its id.", false);
        if (matches.Count > 1)
            return ErrorResult(request, "UI_CONTROL_AMBIGUOUS", $"{matches.Count} controls match; pass the id from ui_snapshot.", false,
                new { Candidates = matches.Take(12).Select(c => UiControlDto(c, popupActive)).ToArray() });
        var target = matches[0];
        string? blocked = UiBlockReason(target);
        if (blocked != null)
            return ErrorResult(request, "UI_CONTROL_FORBIDDEN", blocked, false);
        if (popupActive && !target.InPopup && !allowBehindPopup)
            return ErrorResult(request, "UI_BEHIND_POPUP", "A popup is open; this control is behind it. Answer the popup first (or pass allowBehindPopup).", false,
                new { Control = UiControlDto(target, popupActive) });

        string before = MenuManager.instance?.GetCurrentMenu()?.GetIl2CppType().Name ?? "";
        RecordUiTransition("ui_click_requested", null, target.Path);
        try
        {
            if (target.Kind == "toggle")
            {
                var toggle = target.Selectable.TryCast<Toggle>()!;
                if (mode == "pointer") toggle.OnPointerClick(new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left });
                else toggle.isOn = !toggle.isOn;
            }
            else
            {
                var button = target.Selectable.TryCast<Button>()!;
                if (mode == "pointer") button.OnPointerClick(new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left });
                else button.onClick.Invoke();
            }
            RecordUiTransition("ui_click_returned", null, target.Path);
        }
        catch (Exception ex)
        {
            RecordUiTransition("ui_click_failed", null, target.Path, $"{ex.GetType().Name}: {ex.Message}");
            return ErrorResult(request, "UI_ACTION_FAILED", $"Click failed: {ex.GetType().Name}: {ex.Message}", false);
        }
        UpdateUiState(false);
        return SuccessResult(request, new
        {
            Clicked = UiControlDto(target, popupActive),
            MenuBefore = before,
            MenuAfter = MenuManager.instance?.GetCurrentMenu()?.GetIl2CppType().Name,
            Ui = uiState
        });
    }
}
