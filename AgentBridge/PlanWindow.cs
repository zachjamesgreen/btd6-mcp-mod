using System;
using System.IO;
using System.Text;
using System.Text.Json;
using BTD_Mod_Helper;
using BTD_Mod_Helper.Api;
using BTD_Mod_Helper.Api.Components;
using BTD_Mod_Helper.Api.ModOptions;
using BTD_Mod_Helper.Api.UI;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;
using Il2CppTMPro;
using MelonLoader.Utils;
using UnityEngine;

namespace AgentBridge;

// In-game "Agent Plan" window. An external build driver (btd6-bot tools/apop.mjs) writes its plan state to
// UserData/AgentBridge/plan.json; the window re-reads the file whenever it changes. Read-only: nothing here
// touches the match or the profile.
public sealed class PlanWindow : ModWindow
{
    public override string DisplayName => "Agent Plan";
    public override int DefaultWidth => 1000;
    public override int DefaultHeight => 820;
    public override bool AllowMultiple => false;
    public override bool ShowIconOnWindow => false;
    public override bool ShowIconInDock => false;

    public override void ModifyWindow(ModHelperWindow window)
    {
        var text = window.content.AddText(new Info("PlanText", InfoPreset.FillParent), "", 30, TextAlignmentOptions.TopLeft);
        text.Text.AutoLocalize = false;
        text.Text.font = Fonts.Btd6FontBody;
        text.Text.richText = true;
        text.Text.lineSpacing = 0;
        text.Text.overflowMode = TextOverflowModes.Truncate;
        text.Text.margin = new Vector4(16, 12, 16, 12);
        AgentBridgeMod.OnPlanWindowOpened(window, text);
    }

    public override void OnUpdate(ModHelperWindow window) => AgentBridgeMod.RefreshPlanWindow(window);

    public override void OnClose(ModHelperWindow window) => AgentBridgeMod.OnPlanWindowClosed(window);
}

public sealed partial class AgentBridgeMod
{
    private static readonly ModSettingBool AutoOpenPlanWindow = new(true)
    {
        displayName = "Auto-open Agent Plan window",
        description = "Opens the Agent Plan window in a match while a build driver is publishing UserData/AgentBridge/plan.json. Closing the window keeps it closed for the rest of the match."
    };

    private static readonly ModSettingHotkey TogglePlanWindowHotkey = new(KeyCode.F8)
    {
        displayName = "Toggle Agent Plan window",
        description = "Opens or closes the Agent Plan window in a match."
    };

    private static readonly TimeSpan PlanPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan PlanStaleAfter = TimeSpan.FromSeconds(30);
    private static string PlanStatusPath => Path.Combine(MelonEnvironment.UserDataDirectory, "AgentBridge", "plan.json");

    private static ModHelperWindow? planWindow;
    private static ModHelperText? planText;
    private static DateTime planFileWriteUtc;
    private static DateTime nextPlanPollUtc;
    private static DateTime nextPlanRefreshUtc;
    private static bool planWindowDismissed;
    private static DateTime planMatchSeenUtc;   // first frame of the current match; auto-open waits for its UI to finish loading
    private static readonly TimeSpan PlanOpenDelay = TimeSpan.FromSeconds(3);
    private static string planDocument = "";

    internal static void OnPlanWindowOpened(ModHelperWindow window, ModHelperText text)
    {
        planWindow = window;
        planText = text;
        planFileWriteUtc = default;
        nextPlanRefreshUtc = default;
    }

    internal static void OnPlanWindowClosed(ModHelperWindow window)
    {
        if (!SameWindow(planWindow, window)) return;
        planWindow = null;
        planText = null;
        planWindowDismissed = true;
    }

    // Il2Cpp wrappers for the same native window can be different managed objects.
    private static bool SameWindow(ModHelperWindow? a, ModHelperWindow? b) => a != null && b != null && a.Pointer == b.Pointer;

    private static void ResetPlanWindow()
    {
        planWindow = null;
        planText = null;
        planWindowDismissed = false;
        planMatchSeenUtc = default;
    }

    // Called from OnUpdate: the hotkey, and auto-open while a driver is publishing.
    private static void UpdatePlanWindow()
    {
        if (InGame.instance == null) return;
        if (TogglePlanWindowHotkey.JustPressed())
        {
            if (planWindow != null) planWindow.Close();
            else
            {
                planWindowDismissed = false;
                ModContent.GetInstance<PlanWindow>().Open();
            }
            return;
        }

        var now = DateTime.UtcNow;
        if (now < nextPlanPollUtc) return;
        nextPlanPollUtc = now + PlanPollInterval;
        if (planWindow != null || planWindowDismissed || !AutoOpenPlanWindow || !IsActiveGame()) return;
        // Opening on the match's first frames (a fresh plan.json left by the previous game) built a half-made window whose
        // RectTransforms were null: Mod Helper then threw a NullReferenceException every frame (2026-11-02).
        if (planMatchSeenUtc == default) planMatchSeenUtc = now;
        if (now - planMatchSeenUtc < PlanOpenDelay) return;
        if (!File.Exists(PlanStatusPath) || now - File.GetLastWriteTimeUtc(PlanStatusPath) > PlanStaleAfter) return;
        try
        {
            ModContent.GetInstance<PlanWindow>().Open();
        }
        catch (Exception ex)
        {
            ModHelper.Warning<AgentBridgeMod>($"[AgentBridge] Agent Plan window failed to open; not retrying this match: {ex.Message}");
            try { planWindow?.Close(); } catch { }
            planWindow = null;
            planText = null;
            planWindowDismissed = true;
        }
    }

    internal static void RefreshPlanWindow(ModHelperWindow window)
    {
        if (!SameWindow(planWindow, window) || planText == null) return;
        var now = DateTime.UtcNow;
        if (now < nextPlanRefreshUtc) return;
        nextPlanRefreshUtc = now + PlanPollInterval;

        string rendered;
        try
        {
            if (!File.Exists(PlanStatusPath))
            {
                rendered = "No plan published.\n\nA build driver writes UserData/AgentBridge/plan.json while it plays.";
            }
            else
            {
                var writeUtc = File.GetLastWriteTimeUtc(PlanStatusPath);
                if (writeUtc != planFileWriteUtc)
                {
                    planFileWriteUtc = writeUtc;
                    using var stream = new FileStream(PlanStatusPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    planDocument = reader.ReadToEnd();
                }
                rendered = RenderPlan(planDocument, now - writeUtc);
            }
        }
        catch (Exception exception)
        {
            rendered = $"Could not read plan.json: {exception.Message}";
        }
        planText.Text.SetText(rendered);
    }

    private static string RenderPlan(string json, TimeSpan age)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        string Num(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetRawText() : "?";

        var sb = new StringBuilder();
        var status = Str("status");
        sb.Append($"<b>{Escape(Str("plan"))}</b>   step {Num("step")}/{Num("total")}\n");
        sb.Append($"Round {Num("round")}   Lives {Num("lives")}   Cash ${Num("cash")}\n");
        var stale = status == "running" && age > PlanStaleAfter;
        var statusText = stale ? $"driver silent for {(int)age.TotalSeconds}s" : status;
        var statusColor = stale ? "#ff7070" : status == "running" ? "#8fd18f" : "#c0c0c0";
        sb.Append($"<color={statusColor}>{Escape(statusText)}</color>\n\n");

        var current = Str("current");
        if (current.Length > 0)
        {
            sb.Append($"<color=#ffd34d><b>> {Num("step")}  {Escape(current)}</b></color>\n");
            var pending = Str("pending");
            if (pending.Length > 0) sb.Append($"<color=#ffd34d>    buying tiers: {Escape(pending)}</color>\n");
        }
        else sb.Append("<color=#8fd18f>All steps done.</color>\n");

        if (root.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.Array)
            foreach (var step in next.EnumerateArray())
            {
                var index = step.TryGetProperty("index", out var i) ? i.GetRawText() : "";
                var text = step.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                sb.Append($"<color=#d8d8d8>   {index}  {Escape(text)}</color>\n");
            }

        var paragon = Str("paragon");
        if (paragon.Length > 0) sb.Append($"\n<color=#c9a0ff>{Escape(paragon)}</color>\n");

        if (root.TryGetProperty("recent", out var recent) && recent.ValueKind == JsonValueKind.Array && recent.GetArrayLength() > 0)
        {
            sb.Append("\n<b>Recent</b>\n");
            foreach (var line in recent.EnumerateArray())
                sb.Append($"<color=#a8b4c0>{Escape(line.GetString() ?? "")}</color>\n");
        }
        return sb.ToString();
    }

    // TextMeshPro rich text: keep plan text from opening tags.
    private static string Escape(string text) => text.Replace("<", "<​");
}
