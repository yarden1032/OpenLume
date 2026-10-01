using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenLume.Core.Domain;

[JsonConverter(typeof(ImmutableValuesConverterFactory))]
public sealed class ImmutableValues<T>(IEnumerable<T> values) : IReadOnlyList<T>, IEquatable<ImmutableValues<T>>
{
    private readonly T[] _items = values.ToArray();
    public int Count => _items.Length;
    public T this[int index] => _items[index];
    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public bool Equals(ImmutableValues<T>? other) => other is not null && _items.SequenceEqual(other._items);
    public override bool Equals(object? obj) => obj is ImmutableValues<T> other && Equals(other);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var value in _items) hash.Add(value);
        return hash.ToHashCode();
    }
}

public sealed class ImmutableValuesConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(ImmutableValues<>);
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(ValuesConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()))!;
    private sealed class ValuesConverter<T> : JsonConverter<ImmutableValues<T>>
    {
        public override ImmutableValues<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new(JsonSerializer.Deserialize<T[]>(ref reader, options) ?? []);
        public override void Write(Utf8JsonWriter writer, ImmutableValues<T> value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.ToArray(), options);
    }
}
