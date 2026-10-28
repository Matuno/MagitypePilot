using System.Text;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MagitypePilot;

internal sealed unsafe class Pilot(IGameGui gui, IClientState client, IObjectTable objects, IPluginLog log, GameInput input, DutyQueue queue, Configuration config)
{
    private long next;
    private uint territory;
    private uint instance;
    private bool hadGame;
    private long nextDebugLog;
    private long stopTypingUntil;
    private long nextKeepAlive;
    private long nextKeepAliveWarning;
    internal DirectActions? Direct { get; set; }
    private readonly Dictionary<string, long> bypassPending = new(StringComparer.Ordinal);
    private long bypassStarted;
    public bool Debug { get; set; }
    public bool Armed { get; private set; } = true;
    private string status = "Enabled; waiting for Magitype.";
    public string Status
    {
        get => status;
        private set
        {
            if (status == value) return;
            status = value;
            if (Debug) log.Information("Magitype Pilot: {Status}", value);
        }
    }
    public int CharactersSent => 0;

    public void Arm()
    {
        Armed = true;
        Reset();
        Status = "Enabled; waiting for Magitype.";
    }

    public void Stop(string reason)
    {
        Armed = false;
        Reset();
        Status = reason;
    }

    private void Reset()
    {
        hadGame = false;
        next = 0;
        stopTypingUntil = 0;
        bypassPending.Clear();
        bypassStarted = 0;
    }

    public void Update()
    {
        if (Debug && Ready() && Environment.TickCount64 >= nextDebugLog)
        {
            log.Information("Magitype Pilot activity: {Activity}", input.CaptureActivity());
            nextDebugLog = Environment.TickCount64 + 2000;
        }
        if (Armed) { Direct?.Start(); Direct?.UpdatePlayer(); }
        if (!Armed) return;
        if (ShouldKeepAlive(client.IsLoggedIn, client.IsPvP, client.TerritoryType, Environment.TickCount64, nextKeepAlive))
        {
            nextKeepAlive = Environment.TickCount64 + 5000;
            if (!input.KeepAlive() && Environment.TickCount64 >= nextKeepAliveWarning)
            {
                nextKeepAliveWarning = Environment.TickCount64 + 30000;
                log.Warning("Magitype Pilot: activity watchdog could not verify or reset the game timers.");
            }
        }
        var wait = !client.IsLoggedIn ? "Waiting for login." : client.IsPvP ? "Waiting: PvP is active." :
            !Ready() ? queue.Update(config.AutoExit == true, config.AutoRequeue) ?? "Waiting for Magitype UI." : null;
        if (wait != null)
        {
            if (hadGame) Reset();
            Status = wait;
            return;
        }
        var activity = input.CaptureActivity();
        if (!activity.Available)
        {
            Status = activity.Reason;
            return;
        }
        if (!hadGame || client.TerritoryType != territory || client.Instance != instance)
        {
            Reset();
            territory = client.TerritoryType;
            instance = client.Instance;
            log.Information("Magitype Pilot: game UI ready; starting observation.");
            hadGame = true;
        }
        // Observed penalties arrive after the cast bar disappears.
        if (objects.Any(obj => obj is IBattleChara b && ShouldPrepareStop(b.IsCasting, b.CastActionId, b.CurrentCastTime, b.TotalCastTime)))
            stopTypingUntil = Environment.TickCount64 + 3000;
        if (Environment.TickCount64 < stopTypingUntil ||
            objects.LocalPlayer?.StatusList.Any(effect => effect.StatusId is 5346 or 5437) == true)
        {
            bypassStarted = 0;
            Status = "Stop-typing mechanic: waiting for the effect to finish.";
            return;
        }
        if (Environment.TickCount64 < next) return;
        if (Direct?.Available != true)
        {
            Status = "Direct actions unavailable; retrying validation.";
            next = Environment.TickCount64 + 500;
            return;
        }
        var now = Environment.TickCount64;
        var words = ReadWords(out _);
        foreach (var entry in bypassPending.ToArray())
            if (!words.Contains(entry.Key) || now >= entry.Value) bypassPending.Remove(entry.Key);
        var eligible = EligibleWords(words, bypassPending.Keys);
        string? selected = null;
        nint director = 0;
        nint target = 0;
        foreach (var word in eligible)
        {
            if (!Direct.TryResolve(word, gui, objects, out director, out target)) continue;
            selected = word;
            break;
        }
        if (selected == null)
        {
            if (words.Length == 0) bypassStarted = 0;
            Status = words.Length == 0 ? "Enabled; waiting for a visible phrase." : "Waiting for a clear direct-action target.";
            next = now + 100;
            return;
        }
        bypassStarted = PaceStart(bypassStarted, now, false);
        if (now - bypassStarted < BypassWait(selected.Length, config.TypingDelay))
        {
            Status = "Preparing target at the selected typing speed.";
            next = now + Math.Min(100, Math.Max(1, config.TypingDelay));
            return;
        }
        var sent = Direct.Dispatch(director, target, out var attempted);
        bypassStarted = PaceStart(bypassStarted, now, sent);
        bypassPending[selected] = Environment.TickCount64 + RetryDelay(attempted, config.Speed == TypingSpeed.Ludicrous);
        if (sent) input.RecordBypass();
        if (Debug && attempted)
            log.Information("Magitype Pilot direct action: client result={Result}; awaiting target update.", sent);
        Status = sent ? "Direct action dispatched; awaiting target update." : "Direct action unavailable; retrying.";
    }

    private bool Ready()
    {
        var input = gui.GetAddonByName<AtkUnitBase>("KTGInput", 1);
        var words = gui.GetAddonByName<AtkUnitBase>("KTGEWord", 1);
        return input != null && input->IsVisible && input->IsReady && words != null && words->IsVisible && words->IsReady;
    }

    public GameInput.ActivityStatus CaptureActivity() => input.CaptureActivity();

    private string[] ReadWords(out Dictionary<string, int> ranks)
    {
        ranks = new(StringComparer.Ordinal);
        var addon = gui.GetAddonByName<AtkUnitBase>("KTGEWord", 1);
        if (addon == null) return [];
        var u = &addon->UldManager;
        if (u->NodeList == null || u->NodeListCount > 512) return [];
        var barrierPresent = false;
        var barrierNeedsReinforcement = false;
        foreach (var obj in objects)
        {
            if (obj.ObjectKind != ObjectKind.BattleNpc || obj.BaseId != 20079 || !obj.IsTargetable || obj.IsDead) continue;
            barrierPresent = true;
            barrierNeedsReinforcement |= obj is IBattleChara b && b.MaxHp > 0 && b.CurrentHp < b.MaxHp;
        }
        var words = new List<(string Word, float Y, float CenterX, bool Barrier)>();
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
                var textNode = (AtkTextNode*)text;
                var value = ReadText(textNode->NodeText.StringPtr.Value);
                if (IsSafePhrase(value) && float.IsFinite(node->ScreenY) && float.IsFinite(node->ScreenX))
                    words.Add((value!, node->ScreenY, node->ScreenX + node->Width / 2f,
                        IsBarrierLabel(barrierPresent, textNode->TextColor.R, textNode->TextColor.G, textNode->TextColor.B)));
            }
        }
        if (words.Count == 0) return [];
        var centers = words.Select(word => word.CenterX).ToArray();
        var targets = words.Select(word => (word.Word, word.Y, word.CenterX,
            Rank: word.Barrier ? Math.Min(config.Rank(TargetPriority.Barrier), config.Rank(TargetPriority.Closest)) : config.Rank(TargetPriority.Closest),
            Remaining: float.PositiveInfinity)).ToArray();
        var actionableBarrier = barrierNeedsReinforcement && words.Any(word => word.Barrier);
        foreach (var obj in objects)
        {
            if (obj is not IBattleChara b || b.ObjectKind != ObjectKind.BattleNpc || !b.IsCasting ||
                !gui.WorldToScreen(b.Position, out var position)) continue;
            var isInterrupt = b.CastActionId == 49524 || b.IsCastInterruptible;
            var remaining = b.TotalCastTime - b.CurrentCastTime;
            if (!ShouldPrioritizeCast(isInterrupt, b.CastActionId, remaining, actionableBarrier)) continue;
            var match = InterruptIndex(centers, position.X);
            if (match < 0) continue;
            var target = targets[match];
            var rank = config.Rank(isInterrupt ? TargetPriority.Interruptible : TargetPriority.Casting);
            if (rank < target.Rank) targets[match] = (target.Word, target.Y, target.CenterX, rank,
                float.IsFinite(remaining) && remaining >= 0 ? remaining : float.PositiveInfinity);
        }
        foreach (var target in targets)
            ranks[target.Word] = Math.Min(ranks.GetValueOrDefault(target.Word, int.MaxValue), target.Rank);
        return OrderTargets(targets.Select(t => (t.Word, t.Y, t.Rank, t.Remaining)));
    }

    // The color and object ID were observed together; unknown variants fall back to proximity.
    internal static bool IsBarrierLabel(bool present, byte r, byte g, byte b) => present && r == 160 && g == 248 && b == 116;

    internal static string[] OrderTargets(IEnumerable<(string Word, float Y, int Rank, float Remaining)> targets) =>
        targets.OrderBy(t => t.Rank).ThenBy(t => t.Remaining).ThenByDescending(t => t.Y).Select(t => t.Word).ToArray();

    internal static long BypassWait(int length, int delay) => (long)Math.Max(0, length - 1) * delay;
    internal static long PaceStart(long started, long now, bool sent) => sent ? 0 : started == 0 ? now : started;
    // shortcut: allow the UI to catch up after a request; replace with explicit acceptance if exposed.
    internal static int RetryDelay(bool attempted, bool ludicrous) => attempted ? 250 : ludicrous ? 0 : 100;
    internal static bool ShouldKeepAlive(bool loggedIn, bool pvp, uint territory, long now, long next) =>
        loggedIn && !pvp && territory == 1359 && now >= next;
    internal static string[] EligibleWords(IEnumerable<string> words, IEnumerable<string> pending) => words.Except(pending).ToArray();

    internal static int InterruptIndex(IReadOnlyList<float> centers, float casterX)
    {
        if (!float.IsFinite(casterX)) return -1;
        var best = -1;
        var distance = float.PositiveInfinity;
        var second = float.PositiveInfinity;
        for (var i = 0; i < centers.Count; i++)
        {
            var current = MathF.Abs(centers[i] - casterX);
            if (current < distance) { second = distance; distance = current; best = i; }
            else if (current < second) second = current;
        }
        // Position matching is provisional; skip overlapping or distant labels.
        return distance <= 45 && second - distance >= 20 ? best : -1;
    }

    internal static string[] OrderWords(IEnumerable<(string Word, float Y)> words) =>
        words.OrderByDescending(word => word.Y).Select(word => word.Word).ToArray();

    internal static string? ReadText(byte* p)
    {
        if (p == null) return null;
        var length = 0;
        while (length < 512 && p[length] != 0) length++;
        return length == 512 ? null : Encoding.UTF8.GetString(new ReadOnlySpan<byte>(p, length));
    }
    internal static bool IsSafePhrase(string? value) => value is { Length: > 0 and <= 120 } && value.All(c => !char.IsControl(c) && !char.IsSurrogate(c)) && !string.IsNullOrWhiteSpace(value);
    internal static bool IsUrgentCast(bool interrupt, float remaining) =>
        interrupt || (float.IsFinite(remaining) && remaining is >= 0 and <= 8);
    // An observed long Tankbuster completed despite boss damage; preserve available barrier reinforcement.
    internal static bool ShouldPrioritizeCast(bool interrupt, uint action, float remaining, bool actionableBarrier) =>
        IsUrgentCast(interrupt, remaining) && (interrupt || action != 49699 || !actionableBarrier);
    internal static bool IsStopCast(bool casting, uint action) => casting && action is 49529 or 49532 or 49702;
    internal static bool ShouldPrepareStop(bool casting, uint action, float elapsed, float total) =>
        IsStopCast(casting, action) && (!float.IsFinite(elapsed) || !float.IsFinite(total) || elapsed < 0 || total <= 0 || elapsed / total >= 0.5f);
}
