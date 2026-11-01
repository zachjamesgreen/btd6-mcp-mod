using System;
using System.Linq;
using System.Text.Json;
using BTD_Mod_Helper.Extensions;
using Il2CppAssets.Scripts.Unity.Bridge;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;
using Il2CppAssets.Scripts.Unity.UI_New.InGame.TowerSelectionMenu;
using SimulationParagonTower = Il2CppAssets.Scripts.Simulation.Towers.Behaviors.ParagonTower;

namespace AgentBridge;

public sealed partial class AgentBridgeMod
{
    // Buys a Paragon through the same native call the in-game "Upgrade to Paragon" dialog uses
    // (UnityToSimulation.UpgradeTowerParagon). The cash slider is the dialog's nonUpgradeCashInvestment.
    private static BridgeResultV1 HandleBuyParagon(BridgeRequestV1 request)
    {
        var inGame = InGame.instance;
        if (inGame == null || !inGame.IsInGame())
            return ErrorResult(request, "NO_ACTIVE_GAME", "Cannot buy a paragon: no active match.", false);

        var bridge = inGame.bridge;
        var gameModel = inGame.GetGameModel();
        if (bridge == null || gameModel == null)
            return ErrorResult(request, "SIMULATION_UNAVAILABLE", "Simulation bridge unavailable.", true);
        UpdateUiState(false);
        if (!uiState.Ready)
            return ErrorResult(request, "UI_BLOCKED", "A UI interruption is blocking the paragon purchase. Inspect status.ui.", false);

        string towerId = ReadString(request.Payload, "towerId", "").Trim();
        string paragonType = ReadString(request.Payload, "paragonType", "").Trim();
        const string paragonSuffix = "-Paragon";
        if (paragonType.EndsWith(paragonSuffix, StringComparison.OrdinalIgnoreCase))
            paragonType = paragonType[..^paragonSuffix.Length];
        if (string.IsNullOrEmpty(towerId) && string.IsNullOrEmpty(paragonType))
            return ErrorResult(request, "INVALID_ARGUMENT", "Provide towerId (a tier 5 tower) or paragonType (a native base tower ID such as 'DartMonkey').", false);

        bool useMaxSlider = false;
        double cashSlider = 0d;
        if (request.Payload.ValueKind == JsonValueKind.Object)
        {
            if (request.Payload.TryGetProperty("useMaxSlider", out var maxProp))
            {
                if (maxProp.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return ErrorResult(request, "INVALID_ARGUMENT", "useMaxSlider must be a boolean.", false);
                useMaxSlider = maxProp.GetBoolean();
            }
            if (request.Payload.TryGetProperty("additionalCashSlider", out var sliderProp))
            {
                if (sliderProp.ValueKind != JsonValueKind.Number || !sliderProp.TryGetDouble(out cashSlider) ||
                    !double.IsFinite(cashSlider) || cashSlider < 0d || Math.Truncate(cashSlider) != cashSlider)
                    return ErrorResult(request, "INVALID_ARGUMENT", "additionalCashSlider must be a finite non-negative whole amount of cash.", false);
            }
        }
        if (useMaxSlider && cashSlider > 0d)
            return ErrorResult(request, "INVALID_ARGUMENT", "Use either useMaxSlider or additionalCashSlider, not both.", false);

        TowerToSimulation? tower = null;
        if (!string.IsNullOrEmpty(towerId))
        {
            if (!TryFindTower(inGame, towerId, out tower))
                return ErrorResult(request, "TOWER_NOT_FOUND", $"Tower with ID '{towerId}' not found.", false);
            if (!string.IsNullOrEmpty(paragonType) && !string.Equals(tower!.Def?.baseId, paragonType, StringComparison.Ordinal))
                return ErrorResult(request, "INVALID_ARGUMENT", $"Tower '{towerId}' is a {tower.Def?.baseId}, not a {paragonType}.", false);
        }
        else
        {
            var towers = inGame.GetAllTowerToSim();
            tower = towers?.FirstOrDefault(t => t != null && !t.IsParagon && t.Def != null &&
                string.Equals(t.Def.baseId, paragonType, StringComparison.Ordinal) && Tier5Path(t) >= 0 &&
                t.CanUpgradeToParagon());
            if (tower == null)
                return ErrorResult(request, "PARAGON_UNAVAILABLE", $"No placed tier 5 {paragonType} can be upgraded to a paragon now.", false);
        }

        var def = tower!.Def;
        if (tower.IsParagon || def == null || def.isParagon)
            return ErrorResult(request, "ALREADY_PARAGON", $"Tower '{tower.Id}' is already a paragon.", false);
        string baseId = def.baseId ?? "";
        int pricePath = Tier5Path(tower);
        if (pricePath < 0)
            return ErrorResult(request, "PARAGON_UNAVAILABLE", $"Tower '{tower.Id}' is not a tier 5 tower.", false);

        bool towerCan = tower.CanUpgradeToParagon();
        bool menuCan = TowerSelectionMenuUtils.CanUpgradeToParagon(tower, true);
        if (!towerCan || !menuCan)
            return ErrorResult(request, "PARAGON_UNAVAILABLE",
                $"The game does not offer a paragon for tower '{tower.Id}'. It needs a tier 5 on each path of {baseId}, the paragon unlocked on the account, and no paragon over the limit.",
                false, new { TowerCanUpgrade = towerCan, MenuCanUpgrade = menuCan });

        var paragonUpgrade = gameModel.GetParagonUpgradeForTowerId(baseId);
        var paragonData = gameModel.paragonDegreeDataModel;
        if (paragonUpgrade == null || paragonData == null || !float.IsFinite(paragonUpgrade.cost) || paragonUpgrade.cost <= 0f)
            return ErrorResult(request, "PARAGON_PRICE_UNAVAILABLE", $"The native paragon upgrade or degree data for {baseId} is unavailable.", false);

        float rawPrice;
        try { rawPrice = tower.GetUpgradeCost(pricePath, 6, paragonUpgrade.cost, true); }
        catch (Exception exception)
        {
            return ErrorResult(request, "PARAGON_PRICE_UNAVAILABLE", $"Native paragon price lookup failed: {exception.Message}", true);
        }
        int price = Il2CppAssets.Scripts.Simulation.SMath.Math.RoundToNearestInt(rawPrice, 5);
        if (!float.IsFinite(rawPrice) || price <= 0)
            return ErrorResult(request, "PARAGON_PRICE_UNAVAILABLE", "Native paragon price lookup returned an invalid value.", false);

        // Same cap as project_paragon_degree: the slider's money power is capped at maxPowerFromMoneySpent.
        double maxSliderRaw = (double)paragonData.maxPowerFromMoneySpent * price * (1d + paragonData.paidContributionPenalty) / paragonData.moneySpentOverX;
        if (!double.IsFinite(maxSliderRaw) || maxSliderRaw < 0d || maxSliderRaw > int.MaxValue)
            return ErrorResult(request, "PARAGON_DATA_INVALID", "Native paragon cash-slider parameters exceed the supported range.", false);
        int maxSlider = (int)Math.Floor(maxSliderRaw);

        double cashBefore = inGame.GetCash();
        if (useMaxSlider)
            cashSlider = Math.Max(0d, Math.Min(maxSlider, Math.Floor(cashBefore - price)));
        if (cashSlider > maxSlider)
            return ErrorResult(request, "CASH_SLIDER_OVER_CAP", $"additionalCashSlider cannot exceed the native cash-contribution cap of ${maxSlider}.",
                false, new { MaxCashSlider = maxSlider, ParagonPrice = price });
        if (cashBefore < price + cashSlider)
            return ErrorResult(request, "INSUFFICIENT_CASH", $"The paragon costs ${price} plus ${cashSlider} slider cash; only ${Math.Floor(cashBefore)} is available.",
                false, new { ParagonPrice = price, CashSlider = cashSlider, MaxCashSlider = maxSlider, Cash = cashBefore });

        var sacrificeIds = inGame.GetAllTowerToSim()?
            .Where(t => t != null && t.Id.ToString() != tower.Id.ToString() && !t.IsParagon && string.Equals(t.Def?.baseId, baseId, StringComparison.Ordinal))
            .Select(t => t.Id.ToString()).ToArray() ?? Array.Empty<string>();

        bool callbackReceived = false;
        bool nativeSuccess = false;
        bool previousImmediate = bridge.IsImmediateMode;
        try
        {
            bridge.IsImmediateMode = true;
            bridge.UpgradeTowerParagon(bridge.GetInputId(), tower.Id, cashSlider,
                (Il2CppSystem.Action<bool>)((bool result) =>
                {
                    callbackReceived = true;
                    nativeSuccess = result;
                }));
        }
        catch (Exception exception)
        {
            return ErrorResult(request, "PARAGON_OUTCOME_UNKNOWN", $"Native paragon upgrade outcome is unknown: {exception.Message}", true);
        }
        finally
        {
            bridge.IsImmediateMode = previousImmediate;
        }
        if (!callbackReceived)
            return ErrorResult(request, "PARAGON_OUTCOME_UNKNOWN", "Native paragon upgrade outcome was not reported. Observe before retrying.", true);
        if (!nativeSuccess)
            return ErrorResult(request, "PARAGON_PURCHASE_FAILED", $"The game rejected the paragon upgrade of tower '{tower.Id}'.", false,
                new { ParagonPrice = price, CashSlider = cashSlider, Cash = inGame.GetCash() });

        var paragon = inGame.GetAllTowerToSim()?.FirstOrDefault(t => t != null && t.IsParagon &&
            string.Equals(t.Def?.baseId, baseId, StringComparison.Ordinal));
        int? degree = null;
        try
        {
            var behaviors = paragon?.GetSimTower()?.modelBehaviors;
            for (int i = 0; behaviors != null && i < behaviors.Count && degree == null; i++)
            {
                var behavior = behaviors[i]?.TryCast<SimulationParagonTower>();
                if (behavior != null) degree = behavior.GetCurrentDegree();
            }
        }
        catch { }

        double cashAfter = inGame.GetCash();
        return SuccessResult(request, new
        {
            Purchased = true,
            ParagonType = baseId,
            TowerId = paragon?.Id.ToString() ?? tower.Id.ToString(),
            Degree = degree,
            ParagonPrice = price,
            CashSlider = cashSlider,
            MaxCashSlider = maxSlider,
            CashSpent = cashBefore - cashAfter,
            Cash = cashAfter,
            AbsorbedTowerIds = sacrificeIds,
            Tower = paragon == null ? null : ConvertTower(paragon, gameModel)
        });
    }

    private static int Tier5Path(TowerToSimulation tower)
    {
        var tiers = tower.Def?.tiers;
        for (int path = 0; tiers != null && path < tiers.Length; path++)
            if (tiers[path] >= 5) return path;
        return -1;
    }
}
