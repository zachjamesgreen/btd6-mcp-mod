using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BTD_Mod_Helper;
using BTD_Mod_Helper.Api;
using BTD_Mod_Helper.Api.Components;
using BTD_Mod_Helper.Api.ModOptions;
using BTD_Mod_Helper.Api.UI;
using BTD_Mod_Helper.Extensions;
using Il2CppAssets.Scripts.Models;
using Il2CppAssets.Scripts.Simulation.Track.RoundManagers;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;
using Il2CppAssets.Scripts.Unity.Bridge;
using Il2CppTMPro;
using UnityEngine;

namespace AgentBridge;

// Round preview, including generated freeplay rounds (141+). Rounds up to the fixed list's end are read from
// gameModel.roundSet. Later rounds come from a private FreeplayRoundManager built from the same GameModel and seeded
// with this match's freeplay seed (UnityToSimulation.GetFreeplayRoundSeed), so the game's own generator produces
// them. Read-only: the match's live round manager and its random state are never touched.
public sealed partial class AgentBridgeMod
{
    internal sealed record PreviewGroup(string Bloon, int Count, float Start, float End);
    internal sealed record PreviewRound(int Round, string Source, int TotalBloons, float Duration, List<PreviewGroup> Groups);

    // Emission and group times are simulation frames (round 1: 1050.75 frames = round_info's 17.51 s).
    private const float FramesPerSecond = 60f;

    private static FreeplayRoundManager? previewManager;
    private static int previewManagerSeed;
    private static IntPtr previewManagerModel;

    private static int GetFreeplaySeed()
    {
        var bridge = InGame.instance?.bridge;
        return bridge == null ? 0 : bridge.GetFreeplayRoundSeed();
    }

    private static FreeplayRoundManager GetPreviewManager(GameModel gameModel, int seed)
    {
        if (previewManager == null || previewManagerSeed != seed || previewManagerModel != gameModel.Pointer)
        {
            previewManager = new FreeplayRoundManager(gameModel);
            previewManager.SetSeed(seed);
            // The first GetRoundEmissions on a new manager differs from what the game spawns (lazy group setup);
            // every later call matches the live round exactly (checked on rounds 141, 151, 170, 2026-11-05). Discard one.
            try { previewManager.GetRoundEmissions(140); } catch { }
            previewManagerSeed = seed;
            previewManagerModel = gameModel.Pointer;
        }
        return previewManager;
    }

    // round is 1-based (the number the HUD shows).
    internal static PreviewRound? BuildRoundPreview(GameModel gameModel, int round, int seed)
    {
        var groups = new List<PreviewGroup>();
        string source;
        var fixedRounds = gameModel.roundSet?.rounds;
        // Apopalypse (r10, 2026-11-16): every round comes from the seeded generator, never the fixed list.
        bool generatedOnly = IsApopalypseMatch();
        if (!generatedOnly && fixedRounds != null && round <= fixedRounds.Length)
        {
            source = "fixed";
            var model = fixedRounds[round - 1];
            if (model.groups != null)
                foreach (var g in model.groups)
                    if (g != null) groups.Add(new PreviewGroup(g.bloon, g.count, g.start / FramesPerSecond, g.end / FramesPerSecond));
        }
        else
        {
            source = generatedOnly ? "apopalypse" : "freeplay";
            var emissions = GetPreviewManager(gameModel, seed).GetRoundEmissions(round - 1);
            if (emissions == null) return null;
            // Emissions are single bloons; merge consecutive runs of the same bloon into groups.
            PreviewGroup? current = null;
            foreach (var e in emissions)
            {
                if (e == null) continue;
                if (current != null && current.Bloon == e.bloon)
                    current = current with { Count = current.Count + 1, End = Math.Max(current.End, e.time / FramesPerSecond) };
                else
                {
                    if (current != null) groups.Add(current);
                    current = new PreviewGroup(e.bloon, 1, e.time / FramesPerSecond, e.time / FramesPerSecond);
                }
            }
            if (current != null) groups.Add(current);
        }
        return new PreviewRound(round, source, groups.Sum(g => g.Count), groups.Count == 0 ? 0 : groups.Max(g => g.End), groups);
    }

    internal static bool IsApopalypseMatch()
    {
        try { return string.Equals(InGameData.CurrentGame?.selectedMode, "Apopalypse", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    // Diagnostics: which round manager the live spawner uses (read-only; no method calls on it).
    private static object DescribeLiveRoundManagers(UnityToSimulation bridge)
    {
        try
        {
            var spawner = bridge.Simulation?.Map?.spawner;
            if (spawner == null) return new { error = "no spawner" };
            string? Name(Il2CppSystem.Object? o) => o == null ? null : o.GetIl2CppType()?.Name;
            return new
            {
                roundManager = Name(spawner.roundManager),
                baseRoundManager = Name(spawner.baseRoundManager),
                freeplayRoundManager = Name(spawner.freeplayRoundManager),
            };
        }
        catch (Exception ex) { return new { error = ex.Message }; }
    }

    private static BridgeResultV1 HandleFreeplayRoundInfo(BridgeRequestV1 request)
    {
        var inGame = InGame.instance;
        if (inGame == null || !inGame.IsInGame())
            return ErrorResult(request, "NO_ACTIVE_GAME", "Cannot preview rounds: no active match.", false);
        var bridge = inGame.bridge;
        var gameModel = inGame.GetGameModel();
        if (bridge == null || gameModel == null)
            return ErrorResult(request, "SIMULATION_UNAVAILABLE", "Simulation bridge unavailable.", true);

        int current = bridge.GetCurrentRound() + 1;
        int round = ReadInt(request.Payload, "round", current);
        int count = Math.Clamp(ReadInt(request.Payload, "count", 1), 1, 20);
        if (round < 1 || round > 100000)
            return ErrorResult(request, "ROUND_OUT_OF_RANGE", $"Round {round} must be 1-100000.", false);

        int seed = GetFreeplaySeed();
        var rounds = new List<object>();
        for (int r = round; r < round + count; r++)
        {
            PreviewRound? preview;
            try { preview = BuildRoundPreview(gameModel, r, seed); }
            catch (Exception ex) { return ErrorResult(request, "PREVIEW_FAILED", $"Round {r}: {ex.Message}", false); }
            if (preview == null) continue;
            rounds.Add(new
            {
                round = preview.Round,
                source = preview.Source,
                totalBloons = preview.TotalBloons,
                durationSeconds = preview.Duration,
                groups = preview.Groups.Select(g => new { bloon = g.Bloon, count = g.Count, start = g.Start, end = g.End }).ToList()
            });
        }
        int fixedCount = gameModel.roundSet?.rounds?.Length ?? 0;
        object? active = null;
        if (request.Payload.ValueKind == System.Text.Json.JsonValueKind.Object &&
            request.Payload.TryGetProperty("includeActive", out var ia) && ia.ValueKind == System.Text.Json.JsonValueKind.True)
        {
            // Bloons on the track now: id plus a stable handle, so a caller polling during a round can count each spawn once.
            var list = new List<object>();
            var bloons = bridge.GetAllBloons();
            if (bloons != null)
            {
                var it = bloons.Cast<Il2CppSystem.Collections.IEnumerable>().GetEnumerator();
                while (it.MoveNext())
                {
                    var b = it.Current?.TryCast<BloonToSimulation>();
                    if (b?.Def == null) continue;
                    list.Add(new { id = b.Def.name ?? b.Def.baseId, handle = b.id.data.ToString("x") });
                }
            }
            active = list;
        }
        return SuccessResult(request, new { currentRound = current, freeplaySeed = seed, fixedRoundCount = fixedCount, apopalypse = IsApopalypseMatch(), liveRoundManagers = DescribeLiveRoundManagers(bridge), rounds, activeBloons = active });
    }

    // ---------- on-screen panel ----------

    private static readonly ModSettingBool AutoOpenRoundPreview = new(true)
    {
        displayName = "Auto-open Next Rounds panel",
        description = "Opens the Next Rounds panel at the start of every match. Closing it keeps it closed for the rest of the match."
    };

    private static readonly ModSettingHotkey ToggleRoundPreviewHotkey = new(KeyCode.F9)
    {
        displayName = "Toggle Next Rounds panel",
        description = "Opens or closes the Next Rounds panel in a match."
    };

    private static readonly ModSettingInt RoundPreviewCount = new(3)
    {
        displayName = "Rounds shown in Next Rounds",
        description = "How many upcoming rounds the panel lists.",
        min = 1,
        max = 6
    };

    private static ModHelperWindow? previewWindow;
    private static ModHelperText? previewText;
    private static bool previewDismissed;
    private static DateTime previewMatchSeenUtc;
    private static int previewRenderedFor = -1;
    private static int previewRenderedCount = -1;
    private static DateTime nextPreviewPollUtc;

    internal static void OnPreviewWindowOpened(ModHelperWindow window, ModHelperText text)
    {
        previewWindow = window;
        previewText = text;
        previewRenderedFor = -1;
    }

    internal static void OnPreviewWindowClosed(ModHelperWindow window)
    {
        if (!SameWindow(previewWindow, window)) return;
        previewWindow = null;
        previewText = null;
        previewDismissed = true;
    }

    private static void ResetRoundPreview()
    {
        previewWindow = null;
        previewText = null;
        previewDismissed = false;
        previewMatchSeenUtc = default;
        previewRenderedFor = -1;
        previewManager = null;
    }

    private static void UpdateRoundPreview()
    {
        if (InGame.instance == null) return;
        if (ToggleRoundPreviewHotkey.JustPressed())
        {
            if (previewWindow != null) previewWindow.Close();
            else
            {
                previewDismissed = false;
                ModContent.GetInstance<RoundPreviewWindow>().Open();
            }
            return;
        }
        var now = DateTime.UtcNow;
        if (now < nextPreviewPollUtc) return;
        nextPreviewPollUtc = now + TimeSpan.FromMilliseconds(500);
        if (previewWindow != null || previewDismissed || !AutoOpenRoundPreview || !IsActiveGame()) return;
        if (previewMatchSeenUtc == default) previewMatchSeenUtc = now;
        if (now - previewMatchSeenUtc < TimeSpan.FromSeconds(3)) return;
        try { ModContent.GetInstance<RoundPreviewWindow>().Open(); }
        catch (Exception ex)
        {
            ModHelper.Warning<AgentBridgeMod>($"[AgentBridge] Next Rounds panel failed to open; not retrying this match: {ex.Message}");
            try { previewWindow?.Close(); } catch { }
            previewWindow = null;
            previewText = null;
            previewDismissed = true;
        }
    }

    internal static void RefreshRoundPreview(ModHelperWindow window)
    {
        if (!SameWindow(previewWindow, window) || previewText == null) return;
        var inGame = InGame.instance;
        var bridge = inGame?.bridge;
        var gameModel = inGame?.GetGameModel();
        if (bridge == null || gameModel == null) return;
        // The round the player will start next: during a round that is the following one.
        int next = bridge.GetCurrentRound() + 1 + (bridge.AreRoundsActive() ? 1 : 0);
        int count = RoundPreviewCount;
        if (next == previewRenderedFor && count == previewRenderedCount) return;
        previewRenderedFor = next;
        previewRenderedCount = count;

        var sb = new StringBuilder();
        try
        {
            int seed = GetFreeplaySeed();
            for (int r = next; r < next + count; r++)
            {
                var p = BuildRoundPreview(gameModel, r, seed);
                if (p == null) continue;
                var label = r == next ? "<color=#ffd34d><b>Next: round " + r + "</b></color>" : "<b>Round " + r + "</b>";
                sb.Append($"{label}  <color=#a8b4c0>{p.TotalBloons} bloons, {p.Duration:0.#}s{(p.Source != "fixed" ? ", generated" : "")}</color>\n");
                // Totals per bloon type, biggest threats first.
                foreach (var t in p.Groups.GroupBy(g => g.Bloon).Select(g => (bloon: g.Key, count: g.Sum(x => x.Count)))
                             .OrderByDescending(t => BloonRank(t.bloon)).ThenByDescending(t => t.count))
                    sb.Append($"   {t.count,4} x {PrettyBloon(t.bloon)}\n");
                sb.Append('\n');
            }
        }
        catch (Exception ex) { sb.Append($"Preview failed: {ex.Message}"); }
        previewText.Text.SetText(sb.ToString());
    }

    private static int BloonRank(string id)
    {
        string[] order = { "Bad", "Ddt", "Zomg", "Bfb", "Moab", "Ceramic", "Rainbow", "Zebra", "Lead", "Purple", "Black", "White", "Pink", "Yellow", "Green", "Blue", "Red" };
        for (int i = 0; i < order.Length; i++)
            if (id.StartsWith(order[i], StringComparison.OrdinalIgnoreCase)) return order.Length - i;
        return 0;
    }

    private static string PrettyBloon(string id)
    {
        var mods = new List<string>();
        var b = id;
        foreach (var m in new[] { "Fortified", "Regrow", "Camo" })
            if (b.Contains(m)) { mods.Add(m.ToLowerInvariant()); b = b.Replace(m, ""); }
        var name = b switch { "Bfb" => "BFB", "Zomg" => "ZOMG", "Ddt" => "DDT", "Bad" => "BAD", "Moab" => "MOAB", _ => b };
        var color = b switch
        {
            "Bad" or "Ddt" or "Zomg" or "Bfb" or "Moab" => "#ff8a8a",
            "Ceramic" => "#d9a066",
            "Rainbow" => "#c9a0ff",
            "Lead" => "#b0b0b0",
            "Purple" => "#b070ff",
            _ => "#e0e0e0"
        };
        var tail = mods.Count == 0 ? "" : " <color=#8fd18f>" + string.Join(" ", mods) + "</color>";
        return $"<color={color}>{name}</color>{tail}";
    }
}

public sealed class RoundPreviewWindow : ModWindow
{
    public override string DisplayName => "Next Rounds";
    public override int DefaultWidth => 620;
    public override int DefaultHeight => 760;
    public override bool AllowMultiple => false;
    public override bool ShowIconOnWindow => false;
    public override bool ShowIconInDock => false;

    public override void ModifyWindow(ModHelperWindow window)
    {
        var text = window.content.AddText(new Info("PreviewText", InfoPreset.FillParent), "", 28, TextAlignmentOptions.TopLeft);
        text.Text.AutoLocalize = false;
        text.Text.font = Fonts.Btd6FontBody;
        text.Text.richText = true;
        text.Text.lineSpacing = 0;
        text.Text.overflowMode = TextOverflowModes.Truncate;
        text.Text.margin = new Vector4(16, 12, 16, 12);
        AgentBridgeMod.OnPreviewWindowOpened(window, text);
    }

    public override void OnUpdate(ModHelperWindow window) => AgentBridgeMod.RefreshRoundPreview(window);

    public override void OnClose(ModHelperWindow window) => AgentBridgeMod.OnPreviewWindowClosed(window);
}
