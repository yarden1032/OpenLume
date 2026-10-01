using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenLume.Core.Domain;

/// <summary>Immutable, sequence-valued recipe state; history equality survives JSON round trips.</summary>
[JsonConverter(typeof(LocalMaskCollectionJsonConverter))]
public sealed class LocalMaskCollection(IEnumerable<LocalMask> masks) : IReadOnlyList<LocalMask>, IEquatable<LocalMaskCollection>
{
    private readonly LocalMask[] _items = masks.ToArray();
    public static LocalMaskCollection Empty { get; } = new(Array.Empty<LocalMask>());
    public int Count => _items.Length;
    public LocalMask this[int index] => _items[index];
    public IEnumerator<LocalMask> GetEnumerator() => ((IEnumerable<LocalMask>)_items).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public bool Equals(LocalMaskCollection? other) => other is not null && _items.SequenceEqual(other._items);
    public override bool Equals(object? obj) => obj is LocalMaskCollection other && Equals(other);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var mask in _items) hash.Add(mask);
        return hash.ToHashCode();
    }
}

public sealed class LocalMaskCollectionJsonConverter : JsonConverter<LocalMaskCollection>
{
    public override LocalMaskCollection Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(JsonSerializer.Deserialize<LocalMask[]>(ref reader, options) ?? []);
    public override void Write(Utf8JsonWriter writer, LocalMaskCollection value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value.ToArray(), options);
}
