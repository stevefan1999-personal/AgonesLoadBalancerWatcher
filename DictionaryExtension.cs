public static class DictionaryExtension
{
    public static TValue? GetValueOrDefault<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key) => dictionary.TryGetValue(key, out var value) ? value : default;
}