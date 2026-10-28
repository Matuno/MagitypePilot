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
        if (ImGui.Begin("Magitype Pilot", ref this.open))
        {
            ImGui.TextWrapped(this.pilot.Status);
            if (this.pilot.Armed)
            {
                if (ImGui.Button("Disable")) this.pilot.Stop("Disabled by user.");
            }
            else if (ImGui.Button("Enable")) this.pilot.Arm();
            ImGui.Separator();
            ImGui.TextUnformatted("Target priority (top first)");
            ImGui.TextWrapped("Stop-typing mechanics always take precedence.");
            for (var i = 0; i < this.config.Priorities.Count; i++)
            {
                var item = this.config.Priorities[i];
                if (i > 0 && ImGui.SmallButton($"Up##{item}"))
                { (this.config.Priorities[i - 1], this.config.Priorities[i]) = (item, this.config.Priorities[i - 1]); this.Save(); }
                ImGui.SameLine();
                if (i < this.config.Priorities.Count - 1 && ImGui.SmallButton($"Down##{item}"))
                { (this.config.Priorities[i + 1], this.config.Priorities[i]) = (item, this.config.Priorities[i + 1]); this.Save(); }
                ImGui.SameLine();
                ImGui.TextUnformatted(item switch
                {
                    TargetPriority.Interruptible => "Interruptible casts",
                    TargetPriority.Casting => "Urgent enemy casts",
                    TargetPriority.Barrier => "Barriers (not yet detected)",
                    _ => "Closest targets",
                });
            }
            var ludicrous = this.config.LudicrousSpeed;
            if (ImGui.Checkbox("Ludicrous speed", ref ludicrous)) { this.config.LudicrousSpeed = ludicrous; this.Save(); }
            ImGui.TextWrapped("Normally types at a fast human pace, accelerating for dangerous casts.");
            var mute = this.config.MuteTyping;
            if (ImGui.Checkbox("Mute automated key sounds", ref mute)) { this.config.MuteTyping = mute; this.Save(); }
            if (!this.input.QuietAudioAvailable) ImGui.TextWrapped("Typing sound muting is unavailable on this game build.");
            if (this.saveError != null) ImGui.TextWrapped(this.saveError);
            var debug = this.pilot.Debug;
            if (ImGui.Checkbox("Debug activity logging", ref debug)) this.pilot.Debug = debug;
            if (debug) ImGui.TextUnformatted($"Suppressed sounds: {this.input.MutedSounds}");
            ImGui.TextWrapped("Automatically enabled when loaded. Plays and requeues Magitype; disable here to stop. An existing queue is not cancelled.");
        }
        ImGui.End();
    }

}
