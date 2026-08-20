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

public sealed record EquippedItemSnapshot(uint ItemId, IReadOnlyList<uint> MateriaIds);

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
    public static IReadOnlyList<uint> DesiredMateria(GearItem item) => item.Materia
        .Where(materia => !materia.Locked && materia.Id != 0)
        .Select(materia => materia.Id)
        .ToArray();

    public static bool SupportsMateria(GearItem item) => item.Materia.Count > 0;

    public static bool IsRingSlot(string slot) => slot is "RingLeft" or "RingRight";

    public static IReadOnlyDictionary<string, string> MatchEquippedSlots(
        GearExport export,
        IReadOnlyDictionary<string, uint> equippedItems)
    {
        var matches = new Dictionary<string, string>();
        var availableRingSlots = new HashSet<string>(new[] { "RingLeft", "RingRight" });

        foreach (var (slot, desired) in export.Items)
        {
            if (IsRingSlot(slot))
                continue;

            if (equippedItems.TryGetValue(slot, out var actualItemId) && actualItemId == desired.Id)
                matches[slot] = slot;
        }

        foreach (var slot in new[] { "RingLeft", "RingRight" })
        {
            if (!export.Items.TryGetValue(slot, out var desired))
                continue;

            var matchingSlot = availableRingSlots.FirstOrDefault(candidate =>
                equippedItems.TryGetValue(candidate, out var actualItemId) && actualItemId == desired.Id);
            if (matchingSlot is not null)
            {
                matches[slot] = matchingSlot;
                availableRingSlots.Remove(matchingSlot);
            }
        }

        return matches;
    }

    public static int PreservedMateriaCount(GearItem desired, EquippedItemSnapshot current)
    {
        var currentMateria = current.MateriaIds
            .Where(materiaId => materiaId != 0)
            .ToArray();
        var desiredMateria = DesiredMateria(desired);
        var preservedCount = 0;

        while (preservedCount < currentMateria.Length
            && preservedCount < desiredMateria.Count
            && currentMateria[preservedCount] == desiredMateria[preservedCount])
        {
            preservedCount++;
        }

        return preservedCount;
    }

    public static bool NeedsMateriaChange(GearItem desired, EquippedItemSnapshot current)
    {
        if (!SupportsMateria(desired))
            return false;

        var desiredMateria = DesiredMateria(desired);
        if (desiredMateria.Count == 0)
            return false;

        return !current.MateriaIds
            .Where(materiaId => materiaId != 0)
            .OrderBy(materiaId => materiaId)
            .SequenceEqual(desiredMateria.OrderBy(materiaId => materiaId));
    }

    public static IReadOnlyList<MeldStep> Build(GearExport export)
    {
        var steps = new List<MeldStep>();
        foreach (var (slot, item) in export.Items)
        {
            if (!SupportsMateria(item))
                continue;

            var desiredMateria = DesiredMateria(item);
            for (var index = 0; index < desiredMateria.Count; index++)
            {
                steps.Add(new MeldStep(slot, item.Id, index, desiredMateria[index]));
            }
        }

        return steps;
    }

    public static GearValidationResult Validate(
        GearExport export,
        IReadOnlyDictionary<string, uint> equippedItems)
    {
        var mismatches = new List<GearMismatch>();
        var matches = MatchEquippedSlots(export, equippedItems);

        foreach (var (slot, expectedItem) in export.Items)
        {
            if (!matches.TryGetValue(slot, out var matchedSlot))
            {
                mismatches.Add(new GearMismatch(slot, expectedItem.Id, equippedItems.GetValueOrDefault(slot) is var actualId && actualId != 0 ? actualId : null));
            }
        }

        foreach (var slot in equippedItems.Keys)
        {
            if (!matches.Values.Contains(slot))
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