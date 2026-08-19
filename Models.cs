using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoMeld;

public sealed class GearExport
{
    [JsonPropertyName("name")] public string Name { get; set; } = "Imported gear set";
    [JsonPropertyName("items")] public Dictionary<string, GearItem> Items { get; set; } = new();
}

public sealed class GearItem
{
    [JsonPropertyName("id")] public uint Id { get; set; }
    [JsonPropertyName("materia")] public List<MateriaEntry> Materia { get; set; } = new();
}

public sealed class MateriaEntry
{
    [JsonPropertyName("id")] public uint Id { get; set; }
    [JsonPropertyName("locked")] public bool Locked { get; set; }
}

public sealed record MeldStep(string Slot, uint ItemId, int SlotIndex, uint MateriaId);

public sealed record GearMismatch(string Slot, uint? ExpectedItemId, uint? ActualItemId)
{
    public override string ToString() => ActualItemId is null
        ? $"{Slot}: expected item {ExpectedItemId}, but no gear was found"
        : $"{Slot}: expected item {ExpectedItemId}, found item {ActualItemId}";
}

public sealed class GearValidationResult
{
    public GearValidationResult(IReadOnlyList<GearMismatch> mismatches) => Mismatches = mismatches;

    public IReadOnlyList<GearMismatch> Mismatches { get; }
    public bool IsMatch => Mismatches.Count == 0;
}

public static class GearPlan
{
    public static IReadOnlyList<MeldStep> Build(GearExport export)
    {
        var steps = new List<MeldStep>();
        foreach (var (slot, item) in export.Items)
        {
            for (var index = 0; index < item.Materia.Count; index++)
            {
                var materia = item.Materia[index];
                if (!materia.Locked && materia.Id != 0)
                    steps.Add(new MeldStep(slot, item.Id, index, materia.Id));
            }
        }

        return steps;
    }

    public static GearValidationResult Validate(
        GearExport export,
        IReadOnlyDictionary<string, uint> equippedItems)
    {
        var mismatches = new List<GearMismatch>();

        foreach (var (slot, expectedItem) in export.Items)
        {
            if (!equippedItems.TryGetValue(slot, out var actualItemId))
            {
                mismatches.Add(new GearMismatch(slot, expectedItem.Id, null));
            }
            else if (actualItemId != expectedItem.Id)
            {
                mismatches.Add(new GearMismatch(slot, expectedItem.Id, actualItemId));
            }
        }

        foreach (var slot in equippedItems.Keys)
        {
            if (!export.Items.ContainsKey(slot))
                mismatches.Add(new GearMismatch(slot, null, equippedItems[slot]));
        }

        return new GearValidationResult(mismatches);
    }

    public static GearExport Parse(string json) =>
        JsonSerializer.Deserialize<GearExport>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? throw new JsonException("The export did not contain a gear set.");
}