using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MagitypePilot;

internal sealed unsafe class GameInput(IGameGui gui, IAgentLifecycle agents, ISigScanner scanner)
{
    // Accept these offsets only with the exact analyzed KTG update body.
    private const int ActivityTableRva = 0x2200680;
    private const int ActivityUpdateRva = 0x10FACC0;
    private const int ActivityUpdateLength = 883;
    private const string ActivityUpdateHash = "F2AFDD0B860B7C704E1A6FA8FAA821558F38ABCDC793FD036E1D5FBCAA82631F";
    private bool? codeMatches;
    private int activityUpdates;

    [StructLayout(LayoutKind.Explicit, Size = 12)]
    private struct ActivityCounters
    {
        [FieldOffset(0)] public float Idle;
        [FieldOffset(4)] public float SinceDifferentKey;
        [FieldOffset(8)] public ushort LastKey;
        [FieldOffset(10)] public ushort Repeats;
    }

    internal sealed record ActivityStatus(bool Available, string Reason, float? IdleSeconds,
        float? SinceDifferentKeySeconds, ushort? Repeats, int RecordedInputs);

    private AtkComponentTextInput* GetInput()
    {
        var addon = gui.GetAddonByName<AtkUnitBase>("KTGInput", 1);
        if (addon == null || !addon->IsVisible || !addon->IsReady) return null;
        var component = addon->GetComponentByNodeId(7);
        if (component == null || component->GetComponentType() != ComponentType.TextInput) return null;
        var input = (AtkComponentTextInput*)component;
        return input->OwnerAddon == addon && input->Enabled && input->Callback != null ? input : null;
    }

    public string? ReadText()
    {
        var input = GetInput();
        return input == null ? null : Pilot.ReadText(input->RawString.StringPtr.Value);
    }

    public bool ClearOwnedText(string expected)
    {
        var input = GetInput();
        if (input == null || !CanClear(Pilot.ReadText(input->RawString.StringPtr.Value), expected)) return false;
        input->SetText(string.Empty);
        return Matches(ReadText(), string.Empty);
    }

    private bool TryGetActivity(out ActivityCounters* counters, out string reason)
    {
        counters = null;
        var module = AgentModule.Instance();
        var agent = module == null ? null : module->GetAgentByInternalId((AgentId)398);
        if (agent == null) { reason = "KTG input agent unavailable."; return false; }
        var image = scanner.Module.BaseAddress;
        var table = agents.GetOriginalVirtualTable((nint)agent->VirtualTable);
        if (table == 0) table = (nint)agent->VirtualTable;
        if (table != image + ActivityTableRva || ((nint*)table)[7] != image + ActivityUpdateRva)
        { reason = "KTG activity layout changed; update required."; return false; }
        codeMatches ??= Convert.ToHexString(SHA256.HashData(new ReadOnlySpan<byte>((void*)(image + ActivityUpdateRva), ActivityUpdateLength))) == ActivityUpdateHash;
        if (!codeMatches.Value) { reason = "KTG activity code changed; update required."; return false; }
        var candidate = (ActivityCounters*)((byte*)agent + 0x50);
        if (!ValidCounters(candidate->Idle, candidate->SinceDifferentKey, candidate->LastKey, candidate->Repeats))
        { reason = "KTG activity counters are outside the verified range."; return false; }
        counters = candidate;
        reason = "Verified KTG activity layout.";
        return true;
    }

    public ActivityStatus CaptureActivity()
    {
        var available = TryGetActivity(out var counters, out var reason);
        return new(available, reason, available ? counters->Idle : null,
            available ? counters->SinceDifferentKey : null, available ? counters->Repeats : null, activityUpdates);
    }

    private void RecordAcceptedInput()
    {
        if (GetInput() == null || !TryGetActivity(out var counters, out _)) return;
        counters->Idle = counters->SinceDifferentKey = 0;
        var shared = InputTimerModule.Instance();
        if (shared != null) shared->AfkTimer = shared->ContentInputTimer = shared->InputTimer = 0;
        activityUpdates++;
    }

    public bool TryType(string expected, string next, out string reason)
    {
        if (!TryGetActivity(out _, out reason)) return false;
        var input = GetInput();
        if (input == null) { reason = "Waiting for the Magitype text input."; return false; }
        if (!Matches(Pilot.ReadText(input->RawString.StringPtr.Value), expected))
        { reason = "Input changed; waiting for the input box to clear."; return false; }
        input->SetText(next);
        if (!Matches(ReadText(), next))
        { reason = "Text entry was not confirmed; waiting for the input box to clear."; return false; }
        RecordAcceptedInput();
        reason = "Text entry confirmed.";
        return true;
    }

    public bool TrySubmit(string expected, out string reason)
    {
        if (!TryGetActivity(out _, out reason)) return false;
        var input = GetInput();
        if (input == null || !Matches(Pilot.ReadText(input->RawString.StringPtr.Value), expected))
        { reason = "Input changed before submission; waiting for the input box to clear."; return false; }
        var result = input->Callback(input->OwnerAddon, InputCallbackType.Enter,
            input->RawString.StringPtr, input->EvaluatedString.StringPtr, input->CallbackEventKind);
        // The native callback returns the cleanup action to its caller; it does not clear the field itself.
        if (result == InputCallbackResult.ClearText && GetInput() == input) input->SetText(string.Empty);
        RecordAcceptedInput();
        reason = $"Native submission dispatched ({result}); awaiting target change.";
        return true;
    }

    internal static bool Matches(string? actual, string expected) => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    internal static bool CanClear(string? actual, string expected) => expected.Length > 0 && string.Equals(actual, expected, StringComparison.Ordinal);
    internal static bool ValidCounters(float idle, float different, ushort key, ushort repeats) =>
        float.IsFinite(idle) && float.IsFinite(different) && idle is >= 0 and <= 86400 &&
        different is >= 0 and <= 86400 && key < 160 && repeats <= 10;

}
