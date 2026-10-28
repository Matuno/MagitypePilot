using System.Text;
using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MagitypePilot;

internal sealed unsafe class Pilot(IGameGui gui, IClientState client, IObjectTable objects, IPluginLog log, GameInput input)
{
    private string? phrase;
    private string? submitted;
    private int index;
    private long next;
    private uint territory;
    private uint instance;
    private bool hadGame;
    private long nextDebugLog;
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
        phrase = submitted = null;
        index = 0;
        hadGame = false;
        next = 0;
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
            !Ready() ? "Waiting for Magitype UI." : null;
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
            next = Environment.TickCount64 + 500;
        }
        // Magitype's Cursed Gaze punishes typing until its cast ends.
        if (objects.Any(obj => obj is IBattleChara b && IsGaze(b.IsCasting, b.CastActionId)))
        {
            Status = "Cursed Gaze: pausing typing until the cast ends.";
            return;
        }
        if (Environment.TickCount64 < next) return;
        var words = ReadWords();
        if (submitted != null && !words.Contains(submitted))
        {
            log.Information("Magitype Pilot: submitted phrase is no longer visible; acceptance is not confirmed.");
            submitted = null;
        }
        if (phrase == null)
        {
            var text = input.ReadText();
            if (text == null || text.Length != 0)
            {
                Status = text == null ? "Waiting for the Magitype text input." : "Waiting for the input box to clear.";
                next = Environment.TickCount64 + 100;
                return;
            }
            phrase = words.FirstOrDefault(word => word != submitted && IsSafePhrase(word));
            if (phrase == null)
            {
                Status = submitted == null ? "Enabled; waiting for a visible phrase." : "Waiting for a new phrase.";
                next = Environment.TickCount64 + 100;
                return;
            }
            index = 0;
            log.Information("Magitype Pilot: selected visible phrase with {Length} characters; {Count} candidates.", phrase.Length, words.Length);
        }
        if (!words.Contains(phrase))
        {
            var cleared = input.ClearOwnedText(phrase[..index]);
            phrase = null;
            index = 0;
            Status = cleared ? "Phrase disappeared; cleared the unfinished input." : "Phrase disappeared; waiting for the input box to clear.";
            next = Environment.TickCount64 + 100;
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
            Status = $"Typing {index}/{phrase.Length} characters.";
            next = Environment.TickCount64 + 35;
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
            submitted = phrase;
            phrase = null;
            next = Environment.TickCount64 + 1000;
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

    private string[] ReadWords()
    {
        var addon = gui.GetAddonByName<AtkUnitBase>("KTGEWord", 1);
        if (addon == null) return [];
        var u = &addon->UldManager;
        if (u->NodeList == null || u->NodeListCount > 512) return [];
        var words = new List<(string Word, float Y)>();
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
                var value = ReadText(((AtkTextNode*)text)->NodeText.StringPtr.Value);
                if (IsSafePhrase(value) && float.IsFinite(node->ScreenY)) words.Add((value!, node->ScreenY));
            }
        }
        return OrderWords(words);
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
    internal static bool IsSafePhrase(string? value) => value is { Length: > 0 and <= 120 } && value.All(c => c is >= ' ' and <= '~') && !string.IsNullOrWhiteSpace(value);
    internal static bool IsGaze(bool casting, uint action) => casting && action == 49529;
}
