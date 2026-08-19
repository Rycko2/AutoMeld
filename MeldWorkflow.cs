using Dalamud.Game.Inventory;
using Dalamud.Plugin.Services;

namespace AutoMeld;

public interface IMeldDriver
{
    bool IsReady { get; }
    string Status { get; }
    void Start(GearExport export, IReadOnlyDictionary<string, uint> equippedItems);
    void Stop();
}

public interface IEquipmentReader
{
    bool TryRead(out IReadOnlyDictionary<string, uint> equippedItems, out string error);
}

public sealed class DalamudEquipmentReader : IEquipmentReader
{
    private readonly IGameInventory inventory;

    public DalamudEquipmentReader(IGameInventory inventory) => this.inventory = inventory;

    public bool TryRead(out IReadOnlyDictionary<string, uint> equippedItems, out string error)
    {
        var items = new Dictionary<string, uint>();

        ReadSingle(items, "Weapon", GameInventoryType.ArmoryMainHand);
        ReadSingle(items, "Head", GameInventoryType.ArmoryHead);
        ReadSingle(items, "Body", GameInventoryType.ArmoryBody);
        ReadSingle(items, "Hand", GameInventoryType.ArmoryHands);
        ReadSingle(items, "Legs", GameInventoryType.ArmoryLegs);
        ReadSingle(items, "Feet", GameInventoryType.ArmoryFeets);
        ReadSingle(items, "Ears", GameInventoryType.ArmoryEar);
        ReadSingle(items, "Neck", GameInventoryType.ArmoryNeck);
        ReadSingle(items, "Wrist", GameInventoryType.ArmoryWrist);

        var rings = inventory.GetInventoryItems(GameInventoryType.ArmoryRings);
        items["RingLeft"] = rings.Length > 0 ? rings[0].ItemId : 0;
        items["RingRight"] = rings.Length > 1 ? rings[1].ItemId : 0;

        equippedItems = items;
        error = string.Empty;
        return true;
    }

    private void ReadSingle(Dictionary<string, uint> items, string slot, GameInventoryType inventoryType)
    {
        var item = inventory.GetInventoryItems(inventoryType);
        items[slot] = item.Length > 0 ? item[0].ItemId : 0;
    }
}

public sealed class MeldWorkflow : IMeldDriver
{
    private IReadOnlyList<MeldStep> steps = Array.Empty<MeldStep>();

    public bool IsReady => false;
    public string Status => "The client-specific meld driver is not installed.";

    public void Start(GearExport export, IReadOnlyDictionary<string, uint> equippedItems)
    {
        var validation = GearPlan.Validate(export, equippedItems);
        if (!validation.IsMatch)
        {
            var details = string.Join("; ", validation.Mismatches.Select(mismatch => mismatch.ToString()));
            throw new InvalidOperationException($"Gear verification failed. Nothing was removed or melded. {details}");
        }

        steps = GearPlan.Build(export);
        throw new InvalidOperationException("AutoMeld is ready to validate plans, but its version-specific game driver is not configured yet.");
    }

    public void Stop()
    {
        steps = Array.Empty<MeldStep>();
    }
}