using Dalamud.Interface.Windowing;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;

namespace AutoMeld;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/xlautomeld";
    private readonly string configDirectory;
    private readonly Configuration configuration;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IChatGui chat;
    private readonly ICommandManager commands;
    private readonly IPluginLog log;
    private readonly IDataManager dataManager;
    private readonly WindowSystem windows = new("AutoMeld");
    private readonly MeldWorkflow workflow;
    private readonly IEquipmentReader equipmentReader;
    private readonly AutoMeldWindow mainWindow;
    private readonly ConfigWindow configWindow;
    private GearExport? export;
    private string jsonText = string.Empty;
    private GearValidationResult? previewValidation;
    private IReadOnlyDictionary<string, EquippedItemSnapshot>? previewEquippedItems;
    private string status = "Import a xivgear JSON export to begin.";

    public string Name => "AutoMeld";

    public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commands, IChatGui chat, IGameInventory inventory, IFramework framework, ICondition condition, IPluginLog log, IDataManager dataManager)
    {
        this.pluginInterface = pluginInterface;
        this.commands = commands;
        this.chat = chat;
        this.log = log;
        this.dataManager = dataManager;
        workflow = new MeldWorkflow(framework, condition, log);
        equipmentReader = new DalamudEquipmentReader(inventory, dataManager, log);
        configDirectory = pluginInterface.GetPluginConfigDirectory();
        configuration = Configuration.Load(configDirectory);
        mainWindow = new AutoMeldWindow(this);
        configWindow = new ConfigWindow(this);

        commands.AddHandler(Command, new CommandInfo((_, _) => mainWindow.IsOpen = !mainWindow.IsOpen)
        {
            HelpMessage = "Open the AutoMeld gear plan window.",
        });
        pluginInterface.UiBuilder.Draw += windows.Draw;
        pluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        pluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        windows.AddWindow(mainWindow);
        windows.AddWindow(configWindow);
        log.Information("AutoMeld loaded. Use {Command} to open the window.", Command);
    }

    public void Dispose()
    {
        log.Information("AutoMeld unloading.");
        commands.RemoveHandler(Command);
        pluginInterface.UiBuilder.Draw -= windows.Draw;
        pluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        pluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        workflow.Stop();
        windows.RemoveAllWindows();
    }

    private void ToggleMainUi() => mainWindow.IsOpen = !mainWindow.IsOpen;

    private void ToggleConfigUi() => configWindow.IsOpen = !configWindow.IsOpen;

    private string ItemDisplay(uint itemId)
    {
        if (itemId == 0)
            return "Empty";

        if (dataManager.GetExcelSheet<Item>().TryGetRow(itemId, out var item))
            return item.Name.ToString();

        log.Warning("Unable to resolve item ID {ItemId} in the Lumina Item sheet.", itemId);
        return "Unknown item";
    }

    private string MismatchDisplay(GearMismatch mismatch)
    {
        var expected = mismatch.ExpectedItemId is uint expectedId ? ItemDisplay(expectedId) : "no item expected";
        var actual = mismatch.ActualItemId is uint actualId ? ItemDisplay(actualId) : "nothing equipped";
        return $"{mismatch.Slot}: expected {expected}; found {actual}";
    }

    private static uint[] DesiredMateria(GearItem item) => item.Materia
        .Where(materia => !materia.Locked && materia.Id != 0)
        .Select(materia => materia.Id)
        .ToArray();

    private bool NeedsMateriaChange(string slot, GearItem desired)
    {
        if (previewEquippedItems is null || !previewEquippedItems.TryGetValue(slot, out var current))
            return false;

        return !current.MateriaIds.SequenceEqual(DesiredMateria(desired));
    }

    private string MateriaDisplay(IEnumerable<uint> materiaIds)
    {
        var names = materiaIds.Select(ItemDisplay).ToArray();
        return names.Length == 0 ? "none" : string.Join(", ", names);
    }

    private void Import()
    {
        log.Information("Import requested from pasted JSON ({Length} characters).", jsonText.Length);
        try
        {
            export = GearPlan.Parse(jsonText);
            previewValidation = null;
            previewEquippedItems = null;
            var steps = GearPlan.Build(export);
            status = $"Loaded {export.Name}: {steps.Count} materia steps.";
            log.Information("Imported export {ExportName}: {ItemCount} gear slots and {StepCount} materia steps.", export.Name, export.Items.Count, steps.Count);
        }
        catch (System.Text.Json.JsonException exception)
        {
            export = null;
            status = $"Import failed: {exception.Message}";
            log.Error(exception, "Import failed for pasted JSON.");
        }
    }

    private void VerifyCurrentGear()
    {
        if (export is null)
        {
            status = "Load a gear export first.";
            log.Warning("Gear verification requested before a gear export was loaded.");
            return;
        }

        if (!equipmentReader.TryRead(out var equippedItems, out var readerError))
        {
            previewValidation = null;
            previewEquippedItems = null;
            status = readerError;
            log.Warning("Gear verification failed because equipped gear could not be read: {Reason}", readerError);
            return;
        }

        previewEquippedItems = equippedItems;
        previewValidation = GearPlan.Validate(export, equippedItems.ToDictionary(pair => pair.Key, pair => pair.Value.ItemId));
        status = previewValidation.IsMatch
            ? "Gear verification passed. No gear has been changed."
            : "Gear verification failed. No gear has been changed.";
        log.Information("Gear verification preview completed. Match: {IsMatch}; mismatches: {MismatchCount}.", previewValidation.IsMatch, previewValidation.Mismatches.Count);
    }

    private void Start()
    {
        if (export is null)
        {
            status = "Load a gear export first.";
            log.Warning("Meld start requested before a gear export was loaded.");
            return;
        }

        if (previewValidation is null || !previewValidation.IsMatch)
        {
            status = "Verify current gear successfully before starting.";
            log.Warning("Meld start refused because there is no successful current-gear preview.");
            return;
        }

        try
        {
            log.Information("Meld start requested for export {ExportName}.", export.Name);
            if (!equipmentReader.TryRead(out var equippedItems, out var readerError))
            {
                status = readerError;
                chat.PrintError($"[AutoMeld] {status}");
                log.Warning("Meld start stopped because equipped gear could not be read: {Reason}", readerError);
                return;
            }

            workflow.Start(export, equippedItems);
            status = workflow.Status;
        }
        catch (InvalidOperationException exception)
        {
            status = exception.Message;
            chat.PrintError($"[AutoMeld] {status}");
            log.Warning(exception, "Meld start was stopped safely.");
        }
        catch (Exception exception)
        {
            status = "Unexpected error. See /xllog for details.";
            chat.PrintError($"[AutoMeld] {status}");
            log.Error(exception, "Unexpected error while starting the meld workflow.");
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
            ImGui.Text("Paste a xivgear JSON export and review the planned changes.");
            ImGui.InputTextMultiline("xivgear JSON", ref plugin.jsonText, 200000, new System.Numerics.Vector2(-1, 160));
            if (ImGui.Button("Load pasted JSON")) plugin.Import();
            if (plugin.export is not null)
            {
                var changedSlots = plugin.previewEquippedItems is null
                    ? new List<KeyValuePair<string, GearItem>>()
                    : plugin.export.Items.Where(pair => plugin.NeedsMateriaChange(pair.Key, pair.Value)).ToList();
                var changeCount = changedSlots.Sum(pair => DesiredMateria(pair.Value).Length);
                ImGui.Separator();
                ImGui.Text($"{plugin.export.Name}  |  {changeCount} materia changes");
                if (plugin.previewEquippedItems is null)
                    ImGui.Text("Verify current gear to calculate changes.");
                if (changedSlots.Count > 0 && ImGui.BeginChild("plan", new System.Numerics.Vector2(0, 130), true))
                {
                    foreach (var (slot, item) in changedSlots)
                    {
                        var current = plugin.previewEquippedItems?.GetValueOrDefault(slot);
                        var currentText = plugin.previewEquippedItems is null
                            ? "not verified"
                            : $"current materia: {plugin.MateriaDisplay(current?.MateriaIds ?? Array.Empty<uint>())}";
                        ImGui.BulletText($"{slot}: {plugin.ItemDisplay(item.Id)}; {currentText}; planned materia: {plugin.MateriaDisplay(DesiredMateria(item))}");
                    }
                    ImGui.EndChild();
                }

                if (plugin.previewValidation is { IsMatch: false } validation)
                {
                    ImGui.TextWrapped($"Gear verification failed: {string.Join("; ", validation.Mismatches.Select(plugin.MismatchDisplay))}");
                }
                else if (plugin.previewValidation?.IsMatch == true)
                {
                    ImGui.Text("Gear verification passed. The current equipped item IDs match the pasted plan.");
                }

                if (ImGui.Button("Verify current gear")) plugin.VerifyCurrentGear();
                ImGui.SameLine();
                if (ImGui.Button("Start automatic meld")) plugin.Start();
                ImGui.SameLine();
                if (ImGui.Button("Stop")) plugin.workflow.Stop();
            }

            ImGui.Separator();
            ImGui.TextWrapped($"Workflow: {plugin.workflow.Status}");
            ImGui.TextWrapped(plugin.status);
        }
    }

    private sealed class ConfigWindow : Window
    {
        private readonly Plugin plugin;

        public ConfigWindow(Plugin plugin) : base("AutoMeld Settings###AutoMeldConfig")
        {
            this.plugin = plugin;
            Size = new System.Numerics.Vector2(360, 130);
            SizeCondition = ImGuiCond.FirstUseEver;
        }

        public override void Draw()
        {
            ImGui.TextWrapped("Example settings window. More workflow options can be added here later.");

            var confirmBeforeStarting = plugin.configuration.ConfirmBeforeStarting;
            if (ImGui.Checkbox("Confirm before starting", ref confirmBeforeStarting))
            {
                plugin.configuration.ConfirmBeforeStarting = confirmBeforeStarting;
                plugin.configuration.Save(plugin.configDirectory);
            }

            ImGui.TextWrapped("This setting is reserved for the final confirmation dialog before automation begins.");
        }
    }
}