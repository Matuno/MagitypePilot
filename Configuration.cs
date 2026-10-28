using Dalamud.Configuration;

namespace MagitypePilot;

public enum TypingSpeed { Average, Fast, Ludicrous, NinetyWpm }

public enum TargetPriority { Interruptible, Casting, Barrier, Closest }

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public List<TargetPriority> Priorities { get; set; } = Enum.GetValues<TargetPriority>().ToList();
    public bool LudicrousSpeed { get; set; }
    public TypingSpeed? Speed { get; set; }
    public bool MuteTyping { get; set; }
    public bool AutoRequeue { get; set; } = true;
    public bool? AutoExit { get; set; }

    internal void Normalize()
    {
        AutoExit ??= AutoRequeue;
        Priorities = (Priorities ?? []).Where(Enum.IsDefined).Distinct()
            .Concat(Enum.GetValues<TargetPriority>().Except(Priorities ?? [])).ToList();
        if (Speed is null || !Enum.IsDefined(Speed.Value)) Speed = LudicrousSpeed ? TypingSpeed.Ludicrous : TypingSpeed.Fast;
    }
    internal int TypingDelay => Speed switch { TypingSpeed.Average => 240, TypingSpeed.NinetyWpm => 133, TypingSpeed.Ludicrous => 0, _ => 100 };
    internal int SpeedIndex => Speed switch { TypingSpeed.Average => 0, TypingSpeed.NinetyWpm => 1, TypingSpeed.Ludicrous => 3, _ => 2 };
    internal static TypingSpeed SpeedAt(int index) => index switch { 0 => TypingSpeed.Average, 1 => TypingSpeed.NinetyWpm, 3 => TypingSpeed.Ludicrous, _ => TypingSpeed.Fast };
    internal int Rank(TargetPriority priority) => Priorities.IndexOf(priority);
}
