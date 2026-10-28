using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace MagitypePilot;

internal sealed unsafe class GameInput(IAgentLifecycle agents, ISigScanner scanner)
{
    // Accept these offsets only with the exact analyzed KTG update body.
    private const int ActivityTableRva = 0x2200680;
    private const int ActivityUpdateRva = 0x10FACC0;
    private const int ActivityUpdateLength = 883;
    private const string ActivityUpdateHash = "F2AFDD0B860B7C704E1A6FA8FAA821558F38ABCDC793FD036E1D5FBCAA82631F";
    private bool? codeMatches;
    private int activityUpdates;
    private int keepAliveUpdates;

    [StructLayout(LayoutKind.Explicit, Size = 12)]
    private struct ActivityCounters
    {
        [FieldOffset(0)] public float Idle;
        [FieldOffset(4)] public float SinceDifferentKey;
        [FieldOffset(8)] public ushort LastKey;
        [FieldOffset(10)] public ushort Repeats;
    }

    internal readonly record struct ActivityStatus(bool Available, string Reason, float? IdleSeconds,
        float? SinceDifferentKeySeconds, ushort? Repeats, int RecordedInputs, int KeepAliveUpdates);

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
            available ? counters->SinceDifferentKey : null, available ? counters->Repeats : null, activityUpdates, keepAliveUpdates);
    }

    // The caller limits this to an enabled pilot inside Magitype.
    internal bool KeepAlive()
    {
        if (!ResetActivity()) return false;
        keepAliveUpdates++;
        return true;
    }

    internal void RecordBypass()
    {
        if (ResetActivity()) activityUpdates++;
    }

    private bool ResetActivity()
    {
        if (!TryGetActivity(out var counters, out _)) return false;
        counters->Idle = counters->SinceDifferentKey = 0;
        var shared = InputTimerModule.Instance();
        if (shared == null) return false;
        shared->AfkTimer = shared->ContentInputTimer = shared->InputTimer = 0;
        return true;
    }

    internal static bool ValidCounters(float idle, float different, ushort key, ushort repeats) =>
        float.IsFinite(idle) && float.IsFinite(different) && idle is >= 0 and <= 86400 &&
        different is >= 0 and <= 86400 && key < 160 && repeats <= 10;

}
