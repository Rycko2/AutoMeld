using Dalamud.Interface.Windowing;
using Dalamud.Game.Command;
using Dalamud.Game.Inventory;
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
    private readonly IGameInventory inventory;
    private readonly WindowSystem windows = new("AutoMeld");
    private readonly MeldWorkflow workflow;
    private readonly IEquipmentReader equipmentReader;
    private readonly AutoMeldWindow mainWindow;
    private readonly ConfigWindow configWindow;
    private GearExport? export;
    private string jsonText = string.Empty;
    private GearValidationResult? previewValidation;
    private IReadOnlyDictionary<string, EquippedItemSnapshot>? previewEquippedItems;
    private string? previewMateriaError;
    private string status = "Import a xivgear JSON export to begin.";

    public string Name => "AutoMeld";

    public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commands, IChatGui chat, IGameInventory inventory, IFramework framework, ICondition condition, IPluginLog log, IDataManager dataManager)
    {
        this.pluginInterface = pluginInterface;
        this.commands = commands;
        this.chat = chat;
        this.log = log;
        this.dataManager = dataManager;
        this.inventory = inventory;
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

    private bool NeedsMateriaChange(string slot, GearItem desired)
    {
        if (previewEquippedItems is null || !previewEquippedItems.TryGetValue(slot, out var current))
            return false;

        return GearPlan.NeedsMateriaChange(desired, current);
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
            previewMateriaError = null;
            var steps = GearPlan.Build(export);
            status = $"Loaded {export.Name}: {steps.Count} materia steps.";
            log.Information("Imported export {ExportName}: {ItemCount} gear slots and {StepCount} materia steps.", export.Name, export.Items.Count, steps.Count);
            VerifyCurrentGear();
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
            previewMateriaError = null;
            status = readerError;
            log.Warning("Gear verification failed because equipped gear could not be read: {Reason}", readerError);
            return;
        }

        previewEquippedItems = equippedItems;
        previewValidation = GearPlan.Validate(export, equippedItems.ToDictionary(pair => pair.Key, pair => pair.Value.ItemId));
        previewMateriaError = previewValidation.IsMatch
            ? FindMissingMateria(export, equippedItems)
            : null;
        status = !previewValidation.IsMatch
            ? "Gear verification failed. No gear has been changed."
            : previewMateriaError is null
                ? "Gear verification passed. No gear has been changed."
                : previewMateriaError;
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

            var currentValidation = GearPlan.Validate(export, equippedItems.ToDictionary(pair => pair.Key, pair => pair.Value.ItemId));
            if (!currentValidation.IsMatch)
            {
                status = "Current gear no longer matches the plan. Verify again before starting.";
                chat.PrintError($"[AutoMeld] {status}");
                return;
            }

            var materiaError = FindMissingMateria(export, equippedItems);
            if (materiaError is not null)
            {
                status = materiaError;
                chat.PrintError($"[AutoMeld] {status}");
                log.Warning("Meld start stopped because required materia is unavailable: {Reason}", status);
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

    private string? FindMissingMateria(GearExport desiredPlan, IReadOnlyDictionary<string, EquippedItemSnapshot> equippedItems)
    {
        var required = new Dictionary<uint, long>();
        foreach (var (slot, item) in desiredPlan.Items)
        {
            if (!equippedItems.TryGetValue(slot, out var current) || !GearPlan.NeedsMateriaChange(item, current))
                continue;

            var desiredMateria = GearPlan.DesiredMateria(item);
            var preservedCount = GearPlan.PreservedMateriaCount(item, current);
            foreach (var materiaId in desiredMateria.Skip(preservedCount))
                required[materiaId] = required.GetValueOrDefault(materiaId) + 1;
        }

        var available = new Dictionary<uint, long>();
        foreach (var inventoryType in new[]
        {
            GameInventoryType.Inventory1,
            GameInventoryType.Inventory2,
            GameInventoryType.Inventory3,
            GameInventoryType.Inventory4,
        })
        {
            foreach (var item in inventory.GetInventoryItems(inventoryType))
            {
                if (item.ItemId == 0)
                    continue;

                available[item.ItemId] = available.GetValueOrDefault(item.ItemId) + item.Quantity;
            }
        }

        var missing = required
            .Where(pair => available.GetValueOrDefault(pair.Key) < pair.Value)
            .Select(pair => $"{ItemDisplay(pair.Key)}: need {pair.Value}, have {available.GetValueOrDefault(pair.Key)}")
            .ToArray();
        if (missing.Length == 0)
            return null;

        return $"Materia verification failed. Missing: {string.Join(", ", missing)}";
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
                var changeCount = changedSlots.Sum(pair => GearPlan.DesiredMateria(pair.Value).Count);
                ImGui.Separator();
                ImGui.Text($"{plugin.export.Name}  |  {changeCount} materia changes");
                if (plugin.previewEquippedItems is null)
                    ImGui.Text("Verify current gear to calculate changes.");
                if (changedSlots.Count > 0 && ImGui.BeginChild("plan", new System.Numerics.Vector2(0, 130), true))
                {
                    foreach (var (slot, item) in changedSlots)
                    {
                        var current = plugin.previewEquippedItems?.GetValueOrDefault(slot);
                        ImGui.Text(slot);
                        ImGui.Indent();
                        ImGui.Text($"current: {plugin.MateriaDisplay(current?.MateriaIds ?? Array.Empty<uint>())}");
                        ImGui.Text($"planned: {plugin.MateriaDisplay(GearPlan.DesiredMateria(item))}");
                        ImGui.Unindent();
                    }
                    ImGui.EndChild();
                }

                if (plugin.previewValidation is { IsMatch: false } validation)
                {
                    ImGui.Text("Gear verification failed:");
                    foreach (var mismatch in validation.Mismatches)
                    {
                        ImGui.Text(mismatch.Slot);
                        ImGui.Indent();
                        ImGui.Text($"current: {(mismatch.ActualItemId is uint actualId ? plugin.ItemDisplay(actualId) : "nothing equipped")}");
                        ImGui.Text($"planned: {(mismatch.ExpectedItemId is uint expectedId ? plugin.ItemDisplay(expectedId) : "no item expected")}");
                        ImGui.Unindent();
                    }
                }
                else if (plugin.previewValidation?.IsMatch == true)
                {
                    ImGui.Text("Gear verification passed. The current equipped item IDs match the pasted plan.");
                }

                if (plugin.previewMateriaError is not null)
                    ImGui.TextWrapped(plugin.previewMateriaError);

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