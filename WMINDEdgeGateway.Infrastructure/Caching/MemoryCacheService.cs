using System;
using System.Collections.Concurrent;

namespace WMINDEdgeGateway.Infrastructure.Caching
{
    public interface IMemoryCacheService
    {
        void Set<T>(string key, T value, TimeSpan duration);
        T? Get<T>(string key);
        void PrintCache(); // ✅ Added to interface
    }

    public class MemoryCacheService : IMemoryCacheService
    {
        // Internal cache dictionary with expiry
        private readonly ConcurrentDictionary<string, (object Value, DateTime Expiry)> _cache = new();

        // Store an item in cache with TTL
        public void Set<T>(string key, T value, TimeSpan duration)
        {
            _cache[key] = (value!, DateTime.UtcNow.Add(duration));
        }

        // Retrieve item from cache
        public T? Get<T>(string key)
        {
            if (_cache.TryGetValue(key, out var entry))
            {
                if (entry.Expiry > DateTime.UtcNow)
                    return (T)entry.Value;

                // Remove expired item
                _cache.TryRemove(key, out _);
            }
            return default;
        }

        // Print all cached items to console
        public void PrintCache()
        {
            Console.WriteLine("---- Memory Cache Contents ----");

            foreach (var key in _cache.Keys)
            {
                if (_cache.TryGetValue(key, out var entry) && entry.Expiry > DateTime.UtcNow)
                {
                    Console.WriteLine($"Key: {key}");

                    if (entry.Value is Array arr)
                    {
                        Console.WriteLine($"Value: Array ({arr.Length} items)");
                        foreach (var item in arr)
                            Console.WriteLine($"  - {item}");
                    }
                    else
                    {
                        Console.WriteLine($"Value: {entry.Value}");
                    }
                }
            }

            Console.WriteLine("--------------------------------");
        }
    }
}
