using Dalamud.Interface.Windowing;
using Dalamud.Game.Inventory;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;

namespace AutoMeld;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/xlautomeld";
    private readonly string configDirectory;
    private readonly Configuration configuration;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IChatGui chat;
    private readonly ICommandManager commands;
    private readonly WindowSystem windows = new("AutoMeld");
    private readonly MeldWorkflow workflow = new();
    private readonly IEquipmentReader equipmentReader;
    private readonly AutoMeldWindow mainWindow;
    private GearExport? export;
    private string importPath = string.Empty;
    private string status = "Import a xivgear JSON export to begin.";

    public string Name => "AutoMeld";

    public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commands, IChatGui chat, IGameInventory inventory)
    {
        this.pluginInterface = pluginInterface;
        this.commands = commands;
        this.chat = chat;
        equipmentReader = new DalamudEquipmentReader(inventory);
        configDirectory = pluginInterface.GetPluginConfigDirectory();
        configuration = Configuration.Load(configDirectory);
        importPath = configuration.LastImportPath;
        mainWindow = new AutoMeldWindow(this);

        commands.AddHandler(Command, new CommandInfo((_, _) => mainWindow.IsOpen = !mainWindow.IsOpen)
        {
            HelpMessage = "Open the AutoMeld gear plan window.",
        });
        pluginInterface.UiBuilder.Draw += windows.Draw;
        pluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        windows.AddWindow(mainWindow);
    }

    public void Dispose()
    {
        commands.RemoveHandler(Command);
        pluginInterface.UiBuilder.Draw -= windows.Draw;
        pluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        workflow.Stop();
        windows.RemoveAllWindows();
    }

    private void ToggleMainUi() => mainWindow.IsOpen = !mainWindow.IsOpen;

    private void Import()
    {
        try
        {
            export = GearPlan.Parse(File.ReadAllText(importPath));
            configuration.LastImportPath = importPath;
            configuration.Save(configDirectory);
            status = $"Loaded {export.Name}: {GearPlan.Build(export).Count} materia steps.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            export = null;
            status = $"Import failed: {exception.Message}";
        }
    }

    private void Start()
    {
        if (export is null)
        {
            status = "Load a gear export first.";
            return;
        }

        try
        {
            if (!equipmentReader.TryRead(out var equippedItems, out var readerError))
            {
                status = readerError;
                chat.PrintError($"[AutoMeld] {status}");
                return;
            }

            workflow.Start(export, equippedItems);
            status = workflow.Status;
        }
        catch (InvalidOperationException exception)
        {
            status = exception.Message;
            chat.PrintError($"[AutoMeld] {status}");
        }
    }

    private sealed class AutoMeldWindow : Window
    {
        private readonly Plugin plugin;

        public AutoMeldWindow(Plugin plugin) : base("AutoMeld##Main")
        {
            this.plugin = plugin;
            SizeConstraints = new WindowSizeConstraints { MinimumSize = new System.Numerics.Vector2(520, 280) };
        }

        public override void Draw()
        {
            ImGui.Text("Import a xivgear export and review its materia plan.");
            ImGui.InputText("JSON path", ref plugin.importPath, 512);
            ImGui.SameLine();
            if (ImGui.Button("Load")) plugin.Import();
            var confirmBeforeStarting = plugin.configuration.ConfirmBeforeStarting;
            if (ImGui.Checkbox("Confirm before starting", ref confirmBeforeStarting))
            {
                plugin.configuration.ConfirmBeforeStarting = confirmBeforeStarting;
                plugin.configuration.Save(plugin.configDirectory);
            }

            if (plugin.export is not null)
            {
                var steps = GearPlan.Build(plugin.export);
                ImGui.Separator();
                ImGui.Text($"{plugin.export.Name}  |  {plugin.export.Items.Count} gear pieces  |  {steps.Count} materia entries");
                if (ImGui.BeginChild("plan", new System.Numerics.Vector2(0, 130), true))
                {
                    foreach (var step in steps)
                        ImGui.BulletText($"{step.Slot} slot {step.SlotIndex + 1}: materia {step.MateriaId} on item {step.ItemId}");
                    ImGui.EndChild();
                }

                if (ImGui.Button("Start automatic meld")) plugin.Start();
                ImGui.SameLine();
                if (ImGui.Button("Stop")) plugin.workflow.Stop();
            }

            ImGui.Separator();
            ImGui.TextWrapped(plugin.status);
        }
    }
}