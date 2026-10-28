using System.Text;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MagitypePilot;

internal sealed unsafe class Pilot(IGameGui gui, IClientState client, IObjectTable objects, IPluginLog log, GameInput input, DutyQueue queue, Configuration config)
{
    private string? phrase;
    private readonly Dictionary<string, long> pending = new(StringComparer.Ordinal);
    private int index;
    private long next;
    private uint territory;
    private uint instance;
    private bool hadGame;
    private long nextDebugLog;
    private long stopTypingUntil;
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
            if (!value.StartsWith("Typing ", StringComparison.Ordinal)) log.Information("Magitype Pilot: {Status}", value);
        }
    }
    public int CharactersSent => index;

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
        phrase = null;
        pending.Clear();
        index = 0;
        hadGame = false;
        next = 0;
        stopTypingUntil = 0;
    }

    public void Update()
    {
        if (Debug && Ready() && Environment.TickCount64 >= nextDebugLog)
        {
            log.Information("Magitype Pilot activity: {Activity}", input.CaptureActivity());
            nextDebugLog = Environment.TickCount64 + 2000;
        }
        if (!Armed) return;
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
            Status = "Stop-typing mechanic: waiting for the effect to finish.";
            return;
        }
        if (Environment.TickCount64 < next) return;
        var words = ReadWords(out var ranks);
        foreach (var entry in pending.ToArray())
            if (CanRetrySubmitted(words.Contains(entry.Key), Environment.TickCount64 - entry.Value))
                pending.Remove(entry.Key);
        var eligible = EligibleWords(words, pending.Keys);
        var priority = eligible.FirstOrDefault();
        if (phrase != null && priority != null && phrase != priority && ranks.GetValueOrDefault(priority, int.MaxValue) < ranks.GetValueOrDefault(phrase, int.MaxValue) &&
            (index == 0 ? input.ReadText() == string.Empty : input.ClearOwnedText(phrase[..index])))
        {
            phrase = null;
            index = 0;
            log.Information("Magitype Pilot: switching to a higher-priority target.");
        }
        if (phrase == null)
        {
            var text = input.ReadText();
            if (text == null)
            {
                Status = "Waiting for the Magitype text input.";
                next = Environment.TickCount64 + 100;
                return;
            }
            if (text.Length != 0)
            {
                phrase = FindResumable(eligible, text, null);
                if (phrase != null)
                {
                    index = text.Length;
                    log.Information("Magitype Pilot: resuming a matching partial phrase at {Count} characters.", index);
                }
                else
                {
                    if (input.TrySubmit(text, out var recovery))
                    {
                        log.Information("Magitype Pilot: submitted unmatched input for recovery. {Result}", recovery);
                        next = Environment.TickCount64 + SubmissionDelay(input.ReadText());
                    }
                    else
                    {
                        Status = "Recovering the input field.";
                        next = Environment.TickCount64 + 100;
                    }
                    return;
                }
            }
            else
            {
            phrase = priority;
            if (phrase == null)
            {
                Status = pending.Count == 0 ? "Enabled; waiting for a visible phrase." : "Waiting for a new phrase.";
                next = 0;
                return;
            }
            index = 0;
            log.Information("Magitype Pilot: selected {Kind} phrase with {Length} characters; {Count} candidates.",
                config.Priorities[ranks[phrase]], phrase.Length, words.Length);
            }
        }
        if (!words.Contains(phrase))
        {
            var cleared = index == 0 ? input.ReadText() == string.Empty : input.ClearOwnedText(phrase[..index]);
            phrase = null;
            index = 0;
            Status = cleared ? "Phrase disappeared; cleared the unfinished input." : "Phrase disappeared; waiting for the input box to clear.";
            next = cleared ? 0 : Environment.TickCount64 + 100;
            return;
        }
        if (index < phrase.Length)
        {
            if (!input.TryType(phrase[..index], phrase[..(index + 1)], out var reason))
            {
                phrase = null;
                index = 0;
                Status = reason;
                next = Environment.TickCount64 + 100;
                return;
            }
            index++;
            var pace = config.Speed == TypingSpeed.NinetyWpm ? "90 WPM" : config.Speed?.ToString().ToLowerInvariant();
            Status = $"Typing {index}/{phrase.Length} characters ({pace}).";
            next = index == phrase.Length ? 0 : Environment.TickCount64 + config.TypingDelay;
        }
        else
        {
            if (!input.TrySubmit(phrase, out var reason))
            {
                phrase = null;
                index = 0;
                Status = reason;
                next = Environment.TickCount64 + 100;
                return;
            }
            log.Information("Magitype Pilot: {Count} characters confirmed. {Result}", index, reason);
            pending[phrase] = Environment.TickCount64;
            phrase = null;
            next = Environment.TickCount64 + SubmissionDelay(input.ReadText());
            Status = "Submitted; waiting for the next phrase.";
        }
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

    internal static int SubmissionDelay(string? input) => input == string.Empty ? 0 : 100;
    // shortcut: 250 ms permits observed UI updates; replace with explicit acceptance if exposed.
    internal static bool CanRetrySubmitted(bool stillVisible, long elapsed) => !stillVisible || elapsed >= 250;
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
    internal static string? FindResumable(IEnumerable<string> words, string text, string? submitted) =>
        text.Length == 0 ? null : words.FirstOrDefault(word => word != submitted && word.StartsWith(text, StringComparison.OrdinalIgnoreCase));
    internal static bool IsStopCast(bool casting, uint action) => casting && action is 49529 or 49532 or 49702;
    internal static bool ShouldPrepareStop(bool casting, uint action, float elapsed, float total) =>
        IsStopCast(casting, action) && (!float.IsFinite(elapsed) || !float.IsFinite(total) || elapsed < 0 || total <= 0 || elapsed / total >= 0.5f);
}
