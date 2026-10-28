using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Dalamud.Game.Command;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;

namespace MagitypePilot;

public sealed partial class Plugin : IDalamudPlugin
{
    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IPluginLog log;
#if WORKSPACE_TESTS
    private const int MaximumTestRequestCharacters = 32 * 1024;
    private readonly ICallGateProvider<string> listTests;
    private readonly ICallGateProvider<string, string> runTests;
    private readonly string assemblySha256;
#endif
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commands;
    private readonly Pilot pilot;
    private readonly Configuration config;
    private readonly GameInput input;
    private string? saveError;
    private bool open = true;
    private readonly object sync = new();

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        IFramework framework,
        IClientState clientState,
        IPluginLog log, IGameGui gui, IObjectTable objects, ICondition conditions, ICommandManager commands, IAgentLifecycle agents, ISigScanner scanner, IGameInteropProvider interop)
    {
        this.pluginInterface = pluginInterface;
        this.commands = commands;
        this.config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        this.config.Normalize();
        this.input = new GameInput(gui, agents, scanner, interop, this.config, log);
        this.pilot = new Pilot(gui, clientState, objects, log, this.input, new DutyQueue(gui, clientState, objects, conditions), this.config);
        this.framework = framework;
        this.clientState = clientState;
        this.log = log;
#if WORKSPACE_TESTS
        this.assemblySha256 = GetAssemblySha256(pluginInterface.AssemblyLocation);
        this.listTests = pluginInterface.GetIpcProvider<string>(
            "DalamudWorkspace.ListTests.v1.MagitypePilot");
        this.runTests = pluginInterface.GetIpcProvider<string, string>(
            "DalamudWorkspace.RunTests.v1.MagitypePilot");

        this.listTests.RegisterFunc(() => JsonSerializer.Serialize(new[]
        {
            new { name = "smoke", readOnly = true, description = "Checks the plugin's current live adapters." },
        }));
        try
        {
            this.runTests.RegisterFunc(this.RunWorkspaceTest);
        }
        catch
        {
            this.listTests.UnregisterFunc();
            throw;
        }
#endif

        if (!commands.AddHandler("/magitypepilot", new CommandInfo((_, _) => this.open = true) { HelpMessage = "Open the Magitype Pilot controls." }))
        {
#if WORKSPACE_TESTS
            this.runTests.UnregisterFunc();
            this.listTests.UnregisterFunc();
#endif
            this.input.Dispose();
            throw new InvalidOperationException("Command /magitypepilot is already registered.");
        }
        framework.Update += this.Update;
        pluginInterface.UiBuilder.Draw += this.Draw;
        pluginInterface.UiBuilder.OpenMainUi += this.Open;
        this.log.Information("Magitype Pilot loaded.");
    }

    public void Dispose()
    {
        lock (this.sync) this.pilot.Stop("Plugin unloaded.");
        this.framework.Update -= this.Update;
        this.input.Dispose();
        this.pluginInterface.UiBuilder.Draw -= this.Draw;
        this.pluginInterface.UiBuilder.OpenMainUi -= this.Open;
        this.commands.RemoveHandler("/magitypepilot");
#if WORKSPACE_TESTS
        this.runTests.UnregisterFunc();
        this.listTests.UnregisterFunc();
#endif
        this.log.Information("Magitype Pilot unloaded.");
    }

    private void Save()
    {
        try { this.pluginInterface.SavePluginConfig(this.config); this.saveError = null; }
        catch (Exception ex) { this.log.Error(ex, "Could not save Magitype Pilot settings."); this.saveError = "Settings could not be saved."; }
    }

    private void Update(IFramework _) { lock (this.sync) this.pilot.Update(); }
    private void Open() => this.open = true;
    private void Draw()
    {
        lock (this.sync) this.DrawLocked();
    }
    private void DrawLocked()
    {
        if (!this.open) return;
        var accent = new Vector4(0.96f, 0.73f, 0.30f, 1f);
        using var colors = ImRaii.PushColor(ImGuiCol.WindowBg, new Vector4(0.06f, 0.07f, 0.10f, 0.96f))
            .Push(ImGuiCol.TitleBg, new Vector4(0.09f, 0.10f, 0.14f, 1f))
            .Push(ImGuiCol.TitleBgActive, new Vector4(0.12f, 0.13f, 0.18f, 1f))
            .Push(ImGuiCol.FrameBg, new Vector4(0.15f, 0.17f, 0.22f, 1f))
            .Push(ImGuiCol.FrameBgHovered, new Vector4(0.22f, 0.24f, 0.30f, 1f))
            .Push(ImGuiCol.FrameBgActive, new Vector4(0.27f, 0.28f, 0.33f, 1f))
            .Push(ImGuiCol.Button, new Vector4(0.16f, 0.18f, 0.23f, 1f))
            .Push(ImGuiCol.ButtonHovered, new Vector4(0.25f, 0.26f, 0.30f, 1f))
            .Push(ImGuiCol.ButtonActive, new Vector4(0.31f, 0.29f, 0.25f, 1f))
            .Push(ImGuiCol.CheckMark, accent).Push(ImGuiCol.SliderGrab, accent).Push(ImGuiCol.SliderGrabActive, accent);
        using var style = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(14, 12))
            .Push(ImGuiStyleVar.FramePadding, new Vector2(8, 5)).Push(ImGuiStyleVar.ItemSpacing, new Vector2(8, 7))
            .Push(ImGuiStyleVar.WindowRounding, 8).Push(ImGuiStyleVar.FrameRounding, 5).Push(ImGuiStyleVar.GrabRounding, 5);
        ImGui.SetNextWindowSize(new Vector2(430, 450), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Magitype Pilot", ref this.open))
        {
            ImGui.TextColored(accent, this.pilot.Armed ? "PILOT ACTIVE" : "PILOT DISABLED");
            ImGui.TextWrapped(this.pilot.Status);
            if (this.pilot.Armed)
            {
                if (ImGui.Button("Disable")) this.pilot.Stop("Disabled by user.");
            }
            else if (ImGui.Button("Enable")) this.pilot.Arm();
            ImGui.Separator();
            ImGui.TextColored(accent, "TARGET PRIORITY");
            ImGui.TextDisabled("Top priority first");
            ImGui.TextWrapped("Stop-typing mechanics always take precedence.");
            for (var i = 0; i < this.config.Priorities.Count; i++)
            {
                var item = this.config.Priorities[i];
                ImGui.BeginDisabled(i == 0);
                if (ImGui.SmallButton($"Up##{item}"))
                { (this.config.Priorities[i - 1], this.config.Priorities[i]) = (item, this.config.Priorities[i - 1]); this.Save(); }
                ImGui.EndDisabled();
                ImGui.SameLine();
                ImGui.BeginDisabled(i == this.config.Priorities.Count - 1);
                if (ImGui.SmallButton($"Down##{item}"))
                { (this.config.Priorities[i + 1], this.config.Priorities[i]) = (item, this.config.Priorities[i + 1]); this.Save(); }
                ImGui.EndDisabled();
                ImGui.SameLine();
                ImGui.TextUnformatted(item switch
                {
                    TargetPriority.Interruptible => "Interruptible casts",
                    TargetPriority.Casting => "Urgent enemy casts",
                    TargetPriority.Barrier => "Barrier reinforcement (experimental)",
                    _ => "Closest targets",
                });
            }
            ImGui.Spacing();
            ImGui.TextColored(accent, "PLAY SETTINGS");
            ImGui.TextUnformatted("Typing speed");
            var speedIndex = this.config.SpeedIndex;
            var speedLabel = speedIndex switch { 0 => "Average · 50 WPM", 1 => "90 WPM", 2 => "Fast · 120 WPM", _ => "Ludicrous" };
            ImGui.SetNextItemWidth(-1);
            if (ImGui.SliderInt("##TypingSpeed", ref speedIndex, 0, 3, speedLabel, ImGuiSliderFlags.NoInput))
            { this.config.Speed = Configuration.SpeedAt(speedIndex); this.config.LudicrousSpeed = this.config.Speed == TypingSpeed.Ludicrous; this.Save(); }
            var autoExit = this.config.AutoExit == true;
            if (ImGui.Checkbox("Automatically exit results", ref autoExit)) { this.config.AutoExit = autoExit; this.Save(); }
            var requeue = this.config.AutoRequeue;
            if (ImGui.Checkbox("Automatically requeue", ref requeue)) { this.config.AutoRequeue = requeue; this.Save(); }
            ImGui.TextDisabled("Requeue begins after you return to the lobby.");
            var mute = this.config.MuteTyping;
            if (ImGui.Checkbox("Mute automated key sounds", ref mute)) { this.config.MuteTyping = mute; this.Save(); }
            if (!this.input.QuietAudioAvailable) ImGui.TextWrapped("Typing sound muting is unavailable on this game build.");
            if (this.saveError != null) ImGui.TextWrapped(this.saveError);
            var debug = this.pilot.Debug;
            if (ImGui.Checkbox("Debug activity logging", ref debug)) this.pilot.Debug = debug;
            if (debug) ImGui.TextUnformatted($"Suppressed sounds: {this.input.MutedSounds}");
            ImGui.TextWrapped("Enabled when loaded. Disable to stop play; existing queues are not cancelled.");
        }
        ImGui.End();
    }

}
