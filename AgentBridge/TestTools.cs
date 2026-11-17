using System;
using System.Collections.Generic;
using System.Text.Json;
using BTD_Mod_Helper.Extensions;
using Il2CppAssets.Scripts.Unity;
using Il2CppAssets.Scripts.Unity.Bridge;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;

namespace AgentBridge;

// Mechanics-testing helpers (2026-11-16, fork r11): per-bloon telemetry and spawning specific bloons in Sandbox.
// list_bloons is read-only and works in any match; sandbox_spawn_bloon refuses outside Sandbox.
public sealed partial class AgentBridgeMod
{
    private static BridgeResultV1 HandleListBloons(BridgeRequestV1 request)
    {
        var inGame = InGame.instance;
        if (inGame == null || !inGame.IsInGame() || inGame.bridge == null)
            return ErrorResult(request, "NO_ACTIVE_GAME", "Cannot list bloons: no active match.", false);
        var p = request.Payload;
        int limit = 200;
        string filter = null;
        bool withMutators = true;
        if (p.ValueKind == JsonValueKind.Object)
        {
            if (p.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number) limit = Math.Clamp(l.GetInt32(), 1, 2000);
            if (p.TryGetProperty("baseId", out var f) && f.ValueKind == JsonValueKind.String) filter = f.GetString();
            if (p.TryGetProperty("mutators", out var m) && (m.ValueKind == JsonValueKind.False)) withMutators = false;
        }
        try
        {
            var bridge = inGame.bridge;
            var sim = bridge.Simulation;
            var paths = sim?.Map?.pathManager?.paths;
            var pathIndices = new Dictionary<IntPtr, int>();
            if (paths != null)
                for (int i = 0; i < paths.Count; i++)
                    if (paths[i] != null) pathIndices[paths[i].Pointer] = i;

            var list = new List<object>();
            int total = 0;
            var all = bridge.GetAllBloons();
            if (all != null)
            {
                var e = all.Cast<Il2CppSystem.Collections.IEnumerable>().GetEnumerator();
                while (e.MoveNext())
                {
                    var b = e.Current?.TryCast<BloonToSimulation>();
                    if (b == null || b.Def == null) continue;
                    var def = b.Def;
                    if (filter != null && !string.Equals(def.baseId, filter, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(def.name, filter, StringComparison.OrdinalIgnoreCase)) continue;
                    total++;
                    if (list.Count >= limit) continue;
                    var sb = b.GetSimBloon();
                    if (sb == null) continue;
                    double? progress = NativeTrackProgress.TryGetProgress(sb, out double np) ? Math.Round(np, 5) : null;
                    int pathIndex = -1;
                    if (sb.path != null && pathIndices.TryGetValue(sb.path.Pointer, out int pi)) pathIndex = pi;
                    var pos = sb.Position.data;
                    List<object> muts = null;
                    if (withMutators)
                    {
                        muts = new List<object>();
                        try
                        {
                            var me = sb.mutators?.GetEnumerator();
                            while (me != null && me.MoveNext())
                            {
                                var tm = me.Current;
                                if (tm?.mutator == null) continue;
                                muts.Add(new
                                {
                                    Id = tm.mutator.id,
                                    Type = tm.mutator.GetIl2CppType().Name,
                                    RemoveAt = tm.removeAt,
                                    TotalDuration = tm.totalDuration,
                                    GlueLevel = tm.mutator.glueLevel
                                });
                            }
                        }
                        catch (Exception ex) { muts.Add(new { Error = ex.Message }); }
                    }
                    list.Add(new
                    {
                        Id = sb.Id.Id,
                        Name = def.name,
                        BaseId = def.baseId,
                        Health = sb.health,
                        MaxHealth = def.maxHealth,
                        DistanceTraveled = Math.Round(sb.distanceTraveled, 3),
                        Progress = progress,
                        PathIndex = pathIndex,
                        X = Math.Round(pos.x, 2),
                        Y = Math.Round(pos.y, 2),
                        ModelSpeed = def.speed,
                        SpeedFrames = def.speedFrames,
                        TrackSpeedMultiplier = sb.trackSpeedMultiplier,
                        IsInvulnerable = sb.IsInvulnerable,
                        IsCamo = def.isCamo,
                        IsRegrow = def.isGrow,
                        IsFortified = def.isFortified,
                        IsMoab = def.isMoab,
                        SpawnRound = sb.spawnRound,
                        CreatedAtRoundTime = sb.createdAtRoundTime,
                        LeakDamage = def.leakDamage,
                        Mutators = muts
                    });
                }
            }
            int lives = -1; double cash = -1;
            try { lives = (int)bridge.GetHealth(); } catch { }
            try { cash = bridge.GetCash(); } catch { }
            return SuccessResult(request, new
            {
                ElapsedFrames = bridge.ElapsedTime,
                ElapsedSeconds = Math.Round(bridge.ElapsedTime / 60.0, 3),
                RoundElapsedSeconds = hasRoundElapsedTime ? Math.Round(currentRoundElapsedSeconds, 3) : (double?)null,
                Round = bridge.GetCurrentRound() + 1,
                Lives = lives,
                Cash = cash,
                TotalBloons = total,
                Returned = list.Count,
                Bloons = list
            });
        }
        catch (Exception ex)
        {
            return ErrorResult(request, "LIST_BLOONS_FAILED", $"Failed to list bloons: {ex.Message}", false);
        }
    }

    private static BridgeResultV1 HandleSandboxSpawnBloon(BridgeRequestV1 request)
    {
        var inGame = InGame.instance;
        if (inGame == null || !inGame.IsInGame())
            return ErrorResult(request, "NO_ACTIVE_GAME", "Cannot spawn bloons: no active match.", false);
        if (!string.Equals(InGameData.CurrentGame?.selectedMode, Il2CppAssets.Scripts.Models.Difficulty.ModeType.Sandbox,
                StringComparison.OrdinalIgnoreCase))
            return ErrorResult(request, "SANDBOX_UNAVAILABLE", "sandbox_spawn_bloon only works in Sandbox mode.", false);
        var p = request.Payload;
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("bloonId", out var idProp) || idProp.ValueKind != JsonValueKind.String)
            return ErrorResult(request, "INVALID_ARGUMENT", "bloonId (string, e.g. \"Red\", \"PurpleRegrow\", \"MoabFortified\", \"DdtCamo\") is required.", false);
        string id = idProp.GetString();
        int count = 1;
        float spacing = 0f;
        if (p.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number) count = Math.Clamp(c.GetInt32(), 1, 500);
        if (p.TryGetProperty("spacing", out var s) && s.ValueKind == JsonValueKind.Number) spacing = Math.Max(0f, (float)s.GetDouble());
        try
        {
            var model = Game.instance.model;
            if (model.GetBloon(id) == null)
                return ErrorResult(request, "UNKNOWN_BLOON", $"No bloon model named '{id}'.", false);
            inGame.SpawnBloons(id, count, spacing);
            return SuccessResult(request, new { Spawned = count, BloonId = id, Spacing = spacing, ElapsedFrames = inGame.bridge?.ElapsedTime });
        }
        catch (Exception ex)
        {
            return ErrorResult(request, "SPAWN_BLOON_FAILED", $"Failed to spawn '{id}': {ex.Message}", false);
        }
    }
}
