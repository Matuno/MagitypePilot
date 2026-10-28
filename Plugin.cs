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
    private bool open = true;
    private readonly object sync = new();

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        IFramework framework,
        IClientState clientState,
        IPluginLog log, IGameGui gui, IObjectTable objects, ICommandManager commands, IAgentLifecycle agents, ISigScanner scanner)
    {
        this.pluginInterface = pluginInterface;
        this.commands = commands;
        this.pilot = new Pilot(gui, clientState, objects, log, new GameInput(gui, agents, scanner));
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
        this.pluginInterface.UiBuilder.Draw -= this.Draw;
        this.pluginInterface.UiBuilder.OpenMainUi -= this.Open;
        this.commands.RemoveHandler("/magitypepilot");
#if WORKSPACE_TESTS
        this.runTests.UnregisterFunc();
        this.listTests.UnregisterFunc();
#endif
        this.log.Information("Magitype Pilot unloaded.");
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
            var debug = this.pilot.Debug;
            if (ImGui.Checkbox("Debug activity logging", ref debug)) this.pilot.Debug = debug;
            ImGui.TextWrapped("Automatically enabled when loaded. Uses the Magitype text field directly; disable here to stop.");
        }
        ImGui.End();
    }

}
