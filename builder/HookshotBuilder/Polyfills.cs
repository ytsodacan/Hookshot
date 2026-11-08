// C# language features the newer .NET has built in and .NET Framework 4.8 lacks (records' init-only setters)
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}

namespace HookshotBuilder
{
    // APIs the newer .NET has built in and .NET Framework 4.8 lacks
    static class Compat
    {
        public static TValue? GetValueOrDefault<TKey, TValue>(this Dictionary<TKey, TValue> d, TKey key) where TKey : notnull
            => d.TryGetValue(key, out var v) ? v : default;

        public static bool ContainsIgnoreCase(this string s, string part) => s.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;

        public static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;
    }
}
