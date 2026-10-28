using System.Security.Cryptography;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Dalamud.Game.ClientState.Objects.Enums;

namespace MagitypePilot;

internal sealed unsafe partial class DirectActions(ISigScanner scanner, IPluginLog log, IClientState client, IObjectTable objects) : IDisposable
{
    private bool attemptedStart;
    private bool debug;
    public bool Available { get; private set; }

    partial void StartDiagnostics();
    partial void StopDiagnostics();
    partial void CapturePlayer();
    partial void BeforeAction(uint action, ulong target, ushort previousSequence);
    partial void AfterAction(uint action, ulong target, ushort sequence, bool advanced);

    public void SetDebug(bool enabled)
    {
        debug = enabled;
        if (!enabled) StopDiagnostics();
        else if (Available) StartDiagnostics();
    }

    public void UpdatePlayer()
    {
        if (debug && Available) CapturePlayer();
    }

    public bool Start()
    {
        if (Available) return true;
        if (attemptedStart) return false;
        attemptedStart = true;
        try
        {
            var image = scanner.Module.BaseAddress;
            var matchAddress = image + 0xec8f50;
            var matchBytes = new ReadOnlySpan<byte>((void*)matchAddress, 721).ToArray();
            // A prior diagnostic hook can leave a trampoline; this routine is never called.
            if (matchBytes[0] == 0xff && matchBytes[1] == 0x24 && matchBytes[2] == 0x25)
                new byte[] { 0x48, 0x89, 0x5c, 0x24, 0x18, 0x56, 0x57 }.CopyTo(matchBytes, 0);
            var hash = Convert.ToHexString(SHA256.HashData(matchBytes));
            var submitHash = Convert.ToHexString(SHA256.HashData(new ReadOnlySpan<byte>((void*)(image + 0x10fb130), 1261)));
            var enemyHash = Convert.ToHexString(SHA256.HashData(new ReadOnlySpan<byte>((void*)(image + 0xb2d080), 31)));
            var lookupHash = Convert.ToHexString(SHA256.HashData(new ReadOnlySpan<byte>((void*)(image + 0x835f60), 49)));
            if (hash != "24E8EB3C4EBE31EC41577F1887395B5C2D5657B7D7A1BF9D38BD1699E35D13F3" ||
                submitHash != "6686793C4C5722DA30AE5654D60AC649227513AFDD1DEC474F671C57CDAAD903" ||
                enemyHash != "0BC948D8763B64160D352C10C03738637C47154BC23FB899D32DBA06CF995032" ||
                lookupHash != "EE3C590CBA62B3B566DA252F964B9CEE38850F3C68CBA1F205DD30144955C4CF" ||
                ActionManager.Addresses.UseAction.Value != image + 0x8ecd90)
                throw new InvalidOperationException("The game build does not match the investigated submission path.");
            Available = true;
            if (debug) StartDiagnostics();
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Magitype direct actions unavailable; using typing.");
            return false;
        }
    }

    public bool TryResolve(string phrase, IGameGui gui, IObjectTable objects, out nint directorAddress, out nint targetAddress)
    {
        directorAddress = targetAddress = 0;
        if (!Available) return false;
        if (client.TerritoryType != 1359) return false;
        var stage = gui.GetAddonByName<AtkUnitBase>("KTGEWord", 1);
        if (stage == null || !stage->IsReady || !stage->IsVisible) return false;
        var u = &stage->UldManager;
        if (u->NodeList == null || u->NodeListCount > 512) return false;
        var centers = new List<float>();
        var selected = -1;
        var selectedBarrier = false;
        for (var i = 0; i < u->NodeListCount; i++)
        {
            var node = u->NodeList[i];
            if (node == null || !node->IsVisible() || (int)node->Type < 1000) continue;
            var component = ((AtkComponentNode*)node)->Component;
            if (component == null) continue;
            var nested = &component->UldManager;
            if (nested->NodeList == null || nested->NodeListCount > 512) continue;
            for (var j = 0; j < nested->NodeListCount; j++)
            {
                var text = nested->NodeList[j];
                if (text == null || text->NodeId != 20 || text->Type != NodeType.Text || !text->IsVisible()) continue;
                var value = Pilot.ReadText(((AtkTextNode*)text)->NodeText.StringPtr.Value);
                var center = node->ScreenX + node->Width / 2f;
                if (!Pilot.IsSafePhrase(value) || !float.IsFinite(center)) continue;
                if (value == phrase)
                {
                    if (selected >= 0) return false;
                    selected = centers.Count;
                    var color = ((AtkTextNode*)text)->TextColor;
                    selectedBarrier = Pilot.IsBarrierLabel(true, color.R, color.G, color.B);
                }
                centers.Add(center);
            }
        }
        if (selected < 0) return false;
        var framework = EventFramework.Instance();
        var director = framework == null ? null : framework->GetInstanceContentDirector();
        var d = (byte*)director;
        if (d == null || d[0xdce] != 0x17 || d[0x1fc0] == 0) return false;
        var head = *(byte**)(d + 0x1fe0);
        if (head == null || head[0x19] == 0) return false;
        var best = float.PositiveInfinity;
        var second = float.PositiveInfinity;
        nint target = 0;
        foreach (var obj in objects)
        {
            if (obj.ObjectKind != ObjectKind.BattleNpc || !obj.IsTargetable || obj.IsDead || !CandidateKindMatches(selectedBarrier, obj.BaseId)) continue;
            var node = *(byte**)(head + 8);
            var steps = 0;
            while (node != null && node[0x19] == 0 && steps++ < 128)
            {
                var id = *(uint*)(node + 0x1c);
                if (obj.EntityId == id) break;
                node = *(byte**)(node + (obj.EntityId < id ? 0 : 0x10));
            }
            if (node == null || node[0x19] != 0 || steps > 128 || *(uint*)(node + 0x1c) != obj.EntityId) continue;
            var distance = 0f;
            if (!selectedBarrier)
            {
                if (!gui.WorldToScreen(obj.Position, out var pos)) continue;
                var label = Pilot.InterruptIndex(centers, pos.X);
                if (label != selected) continue;
                distance = MathF.Abs(pos.X - centers[selected]);
            }
            if (distance < best) { second = best; best = distance; target = obj.Address; }
            else if (distance < second) second = distance;
        }
        if (target == 0 || best > 45 || second - best < 20) return false;
        directorAddress = (nint)director;
        targetAddress = target;
        return true;
    }

    internal static bool CandidateKindMatches(bool barrier, uint baseId) => barrier == (baseId == 20079);

    internal static uint ExtraArgument(uint entity, uint word, uint counter, uint sequence) =>
        unchecked(entity - word + counter + sequence + 1u);

    internal static uint ActionFor(bool enemy, bool altered) => enemy ? (altered ? 51027u : 49516u) : 49518u;

    public bool Dispatch(nint director, nint target, out bool attempted)
    {
        attempted = false;
        if (!Available || client.TerritoryType != 1359 || director == 0 || target == 0) return false;
        var d = (byte*)director;
        var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)target;
        if (d[0xdce] != 0x17 || d[0x1fc0] == 0 || native->GetObjectKind() != FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind.BattleNpc) return false;
        var head = *(byte**)(d + 0x1fe0);
        if (head == null || head[0x19] == 0) return false;
        var node = *(byte**)(head + 8);
        var steps = 0;
        while (node != null && node[0x19] == 0 && steps++ < 128)
        {
            var key = *(uint*)(node + 0x1c);
            if (key == native->EntityId) break;
            node = *(byte**)(node + (native->EntityId < key ? 0 : 0x10));
        }
        if (node == null || node[0x19] != 0 || steps > 128 || *(uint*)(node + 0x1c) != native->EntityId) return false;
        if (*(uint*)(d + 0x2028) == native->BaseId && *(uint*)(d + 0x202c) == native->EntityId &&
            *(uint*)(d + 0x2030) == *(uint*)(node + 0x24) && *(uint*)(d + 0x2034) == *(uint*)(node + 0x28)) return false;
        var image = scanner.Module.BaseAddress;
        var lookup = (delegate* unmanaged<uint, byte*>)(void*)(image + 0x835f60);
        var row = lookup(native->BaseId);
        if (row == null || row[0x21] != 6) return false;
        var player = objects.LocalPlayer;
        var manager = ActionManager.Instance();
        if (player == null || manager == null) return false;
        var isEnemy = (delegate* unmanaged<nint, byte>)(void*)(image + 0xb2d080);
        var enemy = isEnemy(target) != 0;
        var altered = player.StatusList.Any(effect => effect.StatusId == 5342);
        var action = ActionFor(enemy, altered);
        var targetId = (ulong)native->GetGameObjectId();
        var extra = ExtraArgument(native->EntityId, *(uint*)(node + 0x28), *(uint*)(d + 0x1fb8), *(uint*)(node + 0x24));
        if (manager->GetActionStatus(ActionType.Action, action, targetId) != 0) return false;
        var beforeSequence = manager->LastUsedActionSequence;
        BeforeAction(action, targetId, beforeSequence);
        attempted = true;
        var result = manager->UseAction(ActionType.Action, action, targetId, extra);
        var sequence = manager->LastUsedActionSequence;
        AfterAction(action, targetId, sequence, sequence != beforeSequence);
        return result;
    }

    public void Dispose()
    {
        StopDiagnostics();
        Available = false;
    }
}
