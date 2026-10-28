using Dalamud.Configuration;

namespace MagitypePilot;

public enum TargetPriority { Interruptible, Casting, Barrier, Closest }

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public List<TargetPriority> Priorities { get; set; } = Enum.GetValues<TargetPriority>().ToList();
    public bool LudicrousSpeed { get; set; }
    public bool MuteTyping { get; set; }

    internal void Normalize() => Priorities = (Priorities ?? []).Where(Enum.IsDefined).Distinct()
        .Concat(Enum.GetValues<TargetPriority>().Except(Priorities ?? [])).ToList();
    internal int Rank(TargetPriority priority) => Priorities.IndexOf(priority);
}
