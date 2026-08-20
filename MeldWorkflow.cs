using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Inventory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using System.Threading;

namespace AutoMeld;

public interface IMeldDriver
{
    bool IsReady { get; }
    bool IsRunning { get; }
    string Status { get; }
    void Start(GearExport export, IReadOnlyDictionary<string, EquippedItemSnapshot> equippedItems);
    void Stop();
}

public interface IEquipmentReader
{
    bool TryRead(out IReadOnlyDictionary<string, EquippedItemSnapshot> equippedItems, out string error);
}

public sealed class DalamudEquipmentReader : IEquipmentReader
{
    private readonly IGameInventory inventory;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;

    public DalamudEquipmentReader(IGameInventory inventory, IDataManager dataManager, IPluginLog log)
    {
        this.inventory = inventory;
        this.dataManager = dataManager;
        this.log = log;
    }

    public bool TryRead(out IReadOnlyDictionary<string, EquippedItemSnapshot> equippedItems, out string error)
    {
        var items = new Dictionary<string, EquippedItemSnapshot>();
        var equipped = inventory.GetInventoryItems(GameInventoryType.EquippedItems);
        log.Debug("Reading equipped gear snapshot: {Count} equipped inventory entries.", equipped.Length);

        ReadSlot(items, equipped, "Weapon", 0);
        ReadSlot(items, equipped, "Head", 2);
        ReadSlot(items, equipped, "Body", 3);
        ReadSlot(items, equipped, "Hand", 4);
        ReadSlot(items, equipped, "Legs", 6);
        ReadSlot(items, equipped, "Feet", 7);
        ReadSlot(items, equipped, "Ears", 8);
        ReadSlot(items, equipped, "Neck", 9);
        ReadSlot(items, equipped, "Wrist", 10);
        ReadSlot(items, equipped, "RingLeft", 11);
        ReadSlot(items, equipped, "RingRight", 12);

        equippedItems = items;
        error = string.Empty;
        log.Information("Equipped gear snapshot read successfully: {Count} tracked slots.", items.Count);
        return true;
    }

    private void ReadSlot(Dictionary<string, EquippedItemSnapshot> items, ReadOnlySpan<GameInventoryItem> equipped, string slot, uint inventorySlot)
    {
        foreach (ref readonly var item in equipped)
        {
            if (item.InventorySlot == inventorySlot)
            {
                items[slot] = new EquippedItemSnapshot(item.ItemId, item.MateriaEntries.Select(ToMateriaItemId).ToArray());
                log.Debug("Equipped slot {Slot} (inventory slot {InventorySlot}) has item ID {ItemId}.", slot, inventorySlot, item.ItemId);
                return;
            }
        }

        items[slot] = new EquippedItemSnapshot(0, Array.Empty<uint>());
        log.Warning("Equipped slot {Slot} (inventory slot {InventorySlot}) was not present.", slot, inventorySlot);
    }

    private uint ToMateriaItemId(Dalamud.Game.Inventory.Records.MateriaEntry entry)
    {
        var materia = dataManager.GetExcelSheet<Materia>().GetRow(entry.Type.RowId);
        var grade = entry.Grade.RowId;
        return grade < materia.Item.Count ? materia.Item[(int)grade].RowId : 0;
    }
}

public sealed class MeldWorkflow : IMeldDriver
{
    private readonly IFramework framework;
    private readonly ICondition condition;
    private readonly IPluginLog log;
    private CancellationTokenSource? cancellation;
    private IReadOnlyList<MeldStep> steps = Array.Empty<MeldStep>();

    public MeldWorkflow(IFramework framework, ICondition condition, IPluginLog log)
    {
        this.framework = framework;
        this.condition = condition;
        this.log = log;
    }

    public bool IsReady => true;
    public bool IsRunning => cancellation is not null;
    public string Status { get; private set; } = "Ready.";

    public void Start(GearExport export, IReadOnlyDictionary<string, EquippedItemSnapshot> equippedItems)
    {
        if (IsRunning)
            throw new InvalidOperationException("A meld workflow is already running.");

        var validation = GearPlan.Validate(export, equippedItems.ToDictionary(pair => pair.Key, pair => pair.Value.ItemId));
        if (!validation.IsMatch)
        {
            var details = string.Join("; ", validation.Mismatches.Select(mismatch => mismatch.ToString()));
            log.Warning("Meld workflow aborted before any changes because gear validation failed: {Details}", details);
            throw new InvalidOperationException($"Gear verification failed. Nothing was removed or melded. {details}");
        }

        steps = GearPlan.Build(export);
        cancellation = new CancellationTokenSource();
        Status = $"Starting: {steps.Count} materia operations.";
        log.Information("Starting meld workflow for {ExportName}: {StepCount} materia operations.", export.Name, steps.Count);
        _ = framework.Run(() => Execute(export, equippedItems), cancellation.Token);
    }

    public void Stop()
    {
        if (cancellation is null)
            return;

        log.Information("Stopping meld workflow.");
        cancellation.Cancel();
        Status = "Stopped.";
    }

    private async Task Execute(GearExport export, IReadOnlyDictionary<string, EquippedItemSnapshot> equippedItems)
    {
        try
        {
            if (condition[ConditionFlag.MeldingMateria] || condition[ConditionFlag.Occupied39])
                throw new InvalidOperationException("The player is already busy with a materia action.");

            var slotMatches = GearPlan.MatchEquippedSlots(export, equippedItems.ToDictionary(pair => pair.Key, pair => pair.Value.ItemId));
            foreach (var (slot, desired) in export.Items)
            {
                cancellation!.Token.ThrowIfCancellationRequested();
                if (!slotMatches.TryGetValue(slot, out var equippedSlot)
                    || !equippedItems.TryGetValue(equippedSlot, out var current)
                    || !GearPlan.NeedsMateriaChange(desired, current))
                    continue;

                await MeldItem(slot, equippedSlot, desired, current, cancellation.Token);
            }

            Status = "Complete.";
            log.Information("Meld workflow completed successfully.");
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped.";
            log.Information("Meld workflow canceled.");
        }
        catch (Exception exception)
        {
            Status = $"Failed: {exception.Message}";
            log.Error(exception, "Meld workflow failed.");
        }
        finally
        {
            cancellation?.Dispose();
            cancellation = null;
        }
    }

    private async Task MeldItem(string plannedSlot, string equippedSlotName, GearItem desired, EquippedItemSnapshot current, CancellationToken token)
    {
        var equippedSlot = GetEquippedSlot(equippedSlotName);
        if (GetEquippedItemId(equippedSlot) != desired.Id)
            throw new InvalidOperationException($"{plannedSlot}: equipped item changed before melding. Nothing was changed for this item.");

        var desiredMateria = GearPlan.DesiredMateria(desired);
        var preservedCount = GearPlan.PreservedMateriaCount(desired, current);

        log.Information("Preparing {Slot} ({EquippedSlot}), item {ItemId}: preserving {PreservedCount} materia and replacing {MateriaCount} materia.", plannedSlot, equippedSlotName, desired.Id, preservedCount, desiredMateria.Count - preservedCount);

        if (GetMateriaCount(equippedSlot) > preservedCount)
            await RetrieveMateria(equippedSlot, preservedCount, token);

        for (var index = preservedCount; index < desiredMateria.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            if (GetEquippedItemId(equippedSlot) != desired.Id)
                throw new InvalidOperationException($"{plannedSlot}: equipped item changed during melding. Workflow stopped before further changes.");

            if (GetEquippedItemId(equippedSlot) != desired.Id)
                throw new InvalidOperationException($"{plannedSlot}: equipped item changed after retrieval.");

            await AttachMateria(equippedSlot, desiredMateria[index], token);
        }
    }

    private async Task RetrieveMateria(uint equippedSlot, int targetEmptySlot, CancellationToken token)
    {
        while (GetMateriaCount(equippedSlot) > targetEmptySlot)
        {
            token.ThrowIfCancellationRequested();
            var itemId = GetEquippedItemId(equippedSlot);
            Status = $"Removing materia from item {itemId}.";
            await OpenAgent(token);
            await SelectItem(equippedSlot, token);
            SendAgentEvent(4, [0, 1, 0, 0, 0]);
            await WaitUntil(() => IsAddonActive("MateriaRetrieveDialog"), "retrieve dialog", token);
            FireCallback("MateriaRetrieveDialog", [0], true);
            await WaitUntil(() => !condition[ConditionFlag.Occupied39], "retrieve completion", token, 10);
            if (GetEquippedItemId(equippedSlot) != itemId)
                throw new InvalidOperationException("Equipped item changed during materia removal.");
        }
    }

    private async Task AttachMateria(uint equippedSlot, uint materiaItemId, CancellationToken token)
    {
        var itemId = GetEquippedItemId(equippedSlot);
        if (itemId == 0)
            throw new InvalidOperationException("Equipped item disappeared before materia attachment.");
        Status = $"Attaching materia {materiaItemId} to item {itemId}.";
        await OpenAgent(token);
        await SelectItem(equippedSlot, token);
        await WaitUntil(() => GetAgentUpdateState() == 0, "materia agent loading", token);
        var materiaIndex = FindMateriaIndex(materiaItemId);
        if (materiaIndex < 0)
            throw new InvalidOperationException($"Materia item {materiaItemId} was not found in the melding agent.");
        SendAgentEvent(0, [2, materiaIndex, 1, 0]);
        await WaitUntil(() => condition[ConditionFlag.MeldingMateria], "meld start", token);
        await WaitUntil(() => IsAddonActive("MateriaAttachDialog"), "attach dialog", token);
        FireCallback("MateriaAttachDialog", [0, 0, 1], true);
        await WaitUntil(() => !condition[ConditionFlag.MeldingMateria], "meld completion", token, 10);
    }

    private async Task OpenAgent(CancellationToken token)
    {
        if (IsAgentActive())
            return;

        bool opened;
        unsafe
        {
            opened = ActionManager.Instance()->UseAction(ActionType.GeneralAction, 13);
        }

        if (!opened)
            throw new InvalidOperationException("Unable to open the materia melding window. Ensure materia melding is unlocked.");

        await WaitUntil(IsAgentActive, "open materia agent", token);
    }

    private async Task SelectItem(uint equippedSlot, CancellationToken token)
    {
        SelectEquippedCategory();

        await WaitUntil(() => GetAgentUpdateState() == 0, "load equipped items", token);
        var itemIndex = FindEquippedItemIndex(equippedSlot);
        if (itemIndex < 0)
            throw new InvalidOperationException($"Equipped item in slot {equippedSlot} was not found in the materia agent.");
        SendAgentEvent(0, [1, itemIndex, 1, 0]);
    }

    private async Task WaitUntil(Func<bool> ready, string operation, CancellationToken token, int timeoutSeconds = 5)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (!ready())
        {
            token.ThrowIfCancellationRequested();
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"Timed out waiting for {operation}.");
            await framework.DelayTicks(1, token);
        }
    }

    private static uint GetEquippedSlot(string slot)
    {
        return slot switch
        {
            "Weapon" => 0u,
            "Head" => 2u,
            "Body" => 3u,
            "Hand" => 4u,
            "Legs" => 6u,
            "Feet" => 7u,
            "Ears" => 8u,
            "Neck" => 9u,
            "Wrist" => 10u,
            "RingLeft" => 11u,
            "RingRight" => 12u,
            _ => throw new InvalidOperationException($"Unsupported gear slot {slot}.")
        };
    }

    private static unsafe uint GetEquippedItemId(uint inventorySlot)
    {
        var container = InventoryManager.Instance()->GetInventoryContainer(InventoryType.EquippedItems);
        var item = inventorySlot < container->Size ? container->GetInventorySlot((int)inventorySlot) : null;
        return item == null ? 0 : item->ItemId;
    }

    private static unsafe int GetMateriaCount(uint inventorySlot)
    {
        var container = InventoryManager.Instance()->GetInventoryContainer(InventoryType.EquippedItems);
        var item = inventorySlot < container->Size ? container->GetInventorySlot((int)inventorySlot) : null;
        return item == null ? 0 : item->GetMateriaCount();
    }

    private static unsafe bool IsAgentActive() => AgentMateriaAttach.Instance()->IsAgentActive();

    private static unsafe uint GetAgentUpdateState() => (uint)AgentMateriaAttach.Instance()->UpdateState;

    private static unsafe void SelectEquippedCategory()
    {
        var agent = AgentMateriaAttach.Instance();
        if (agent->Category != AgentMateriaAttach.FilterCategory.Equipped)
            SendAgentEvent(0, [0, (int)AgentMateriaAttach.FilterCategory.Equipped]);
    }

    private static unsafe int FindMateriaIndex(uint materiaItemId)
    {
        var agent = AgentMateriaAttach.Instance();
        for (var index = 0; index < agent->MateriaCount; index++)
        {
            var item = agent->Data->MateriaSorted[index].Value->Item;
            if (item != null && item->ItemId == materiaItemId)
                return index;
        }

        return -1;
    }

    private static unsafe int FindEquippedItemIndex(uint equippedSlot)
    {
        var target = GetEquippedItemAddress(equippedSlot);
        if (target == 0)
            return -1;

        var agent = AgentMateriaAttach.Instance();
        for (var index = 0; index < agent->ItemCount; index++)
        {
            var item = agent->Data->ItemsSorted[index].Value->Item;
            if (item != null && (nint)item == target)
                return index;
        }

        return -1;
    }

    private static unsafe nint GetEquippedItemAddress(uint inventorySlot)
    {
        var container = InventoryManager.Instance()->GetInventoryContainer(InventoryType.EquippedItems);
        var item = inventorySlot < container->Size ? container->GetInventorySlot((int)inventorySlot) : null;
        return (nint)item;
    }

    private static unsafe void SendAgentEvent(ulong eventKind, int[] args)
    {
        var agent = AgentMateriaAttach.Instance();
        var values = stackalloc AtkValue[args.Length];
        for (var index = 0; index < args.Length; index++)
        {
            values[index].Type = AtkValueType.Int;
            values[index].Int = args[index];
        }

        var result = new AtkValue();
        agent->AgentInterface.ReceiveEvent(&result, values, (uint)args.Length, eventKind);
    }

    private static unsafe bool IsAddonActive(string name)
    {
        var addon = RaptureAtkUnitManager.Instance()->GetAddonByName(name);
        return addon != null && addon->IsVisible && addon->IsReady;
    }

    private static unsafe void FireCallback(string name, int[] args, bool close)
    {
        var addon = RaptureAtkUnitManager.Instance()->GetAddonByName(name);
        if (addon == null || !addon->IsVisible || !addon->IsReady)
            throw new InvalidOperationException($"Expected addon {name} was not active.");

        var values = stackalloc AtkValue[args.Length];
        for (var index = 0; index < args.Length; index++)
        {
            values[index].Type = AtkValueType.Int;
            values[index].Int = args[index];
        }

        addon->FireCallback((uint)args.Length, values, close);
    }

}
