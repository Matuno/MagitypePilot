using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Numerics;

namespace MagitypePilot;

internal sealed unsafe class DutyQueue(IGameGui gui, IClientState client, IObjectTable objects, ICondition conditions)
{
    private long next;
    private uint territory;
    private bool registrationRequested;

    public string? Update(bool autoExit, bool autoRequeue)
    {
        if (territory != client.TerritoryType)
        {
            territory = client.TerritoryType;
            next = 0;
            registrationRequested = false;
        }
        if (!ShouldHandle(client.TerritoryType, autoExit, autoRequeue))
        {
            next = 0;
            registrationRequested = false;
            return null;
        }
        if (client.TerritoryType == 1359)
        {
            var result = gui.GetAddonByName<AtkUnitBase>("KTGResult", 1);
            if (result == null || !result->IsVisible || !result->IsReady) { next = 0; return null; }
            if (Environment.TickCount64 < next) return "Leaving the Magitype results.";
            var button = result->GetComponentByNodeId(48);
            var node = result->GetNodeById(48);
            if (node == null || !node->IsVisible() || button == null || button->GetComponentType() != ComponentType.Button ||
                !((AtkComponentButton*)button)->IsEnabled) return "Waiting for the results Exit button.";
            result->FireCallbackInt(-1);
            next = Environment.TickCount64 + 5000;
            return "Leaving the Magitype results.";
        }
        if (client.TerritoryType != 1360) { next = 0; return null; }
        if (conditions[ConditionFlag.InDutyQueue] || conditions[ConditionFlag.WaitingForDuty])
            return "Registered; waiting for Magitype.";
        var addon = gui.GetAddonByName<AtkUnitBase>("KTGEnterDialogue", 1);
        if (addon != null && addon->IsVisible && addon->IsReady)
        {
            if (!CanRegister(registrationRequested, Environment.TickCount64, next)) return "Registration requested; waiting for the queue.";
            var component = addon->GetComponentByNodeId(9);
            if (component == null || component->GetComponentType() != ComponentType.Button ||
                !((AtkComponentButton*)component)->IsEnabled) return "Waiting for the Register button.";
            addon->FireCallbackInt(0);
            registrationRequested = true;
            next = Environment.TickCount64 + 5000;
            return "Registration requested; waiting for the queue.";
        }
        if (Environment.TickCount64 < next) return "Preparing to register for Magitype.";
        if (conditions[ConditionFlag.OccupiedInQuestEvent] || conditions[ConditionFlag.OccupiedInCutSceneEvent] ||
            conditions[ConditionFlag.BetweenAreas]) return "Waiting for the current interaction to finish.";
        var player = objects.LocalPlayer;
        if (player == null) return "Waiting for the player.";
        var npc = objects.FirstOrDefault(o => o != null && IsRegistrar(o.BaseId, o.ObjectKind, o.IsTargetable,
            Vector3.Distance(player.Position, o.Position)));
        if (npc == null || npc.Address == 0) return "Waiting near the Magitype registrar.";
        var target = TargetSystem.Instance();
        if (target == null) return "Waiting for the target system.";
        target->InteractWithObject((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)npc.Address, true);
        registrationRequested = false;
        next = Environment.TickCount64 + 5000;
        return "Opening the Magitype registrar.";
    }

    internal static bool ShouldHandle(uint territory, bool autoExit, bool autoRequeue) =>
        territory == 1359 ? autoExit : territory == 1360 && autoRequeue;

    internal static bool CanRegister(bool requested, long now, long retryAt) => !requested || now >= retryAt;

    internal static bool IsRegistrar(uint id, ObjectKind kind, bool targetable, float distance) =>
        id == 1058751 && kind == ObjectKind.EventNpc && targetable && float.IsFinite(distance) && distance is >= 0 and <= 5;
}
