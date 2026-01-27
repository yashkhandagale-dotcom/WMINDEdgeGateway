using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;

namespace WMINDEdgeGateway.Infrastructure.Caching
{
    public class MemoryCacheService
    {
        private readonly MemoryCache _cache = new(new MemoryCacheOptions());
        private readonly ConcurrentDictionary<string, bool> _keys = new();

        public void Set<T>(string key, T value, TimeSpan ttl)
        {
            _cache.Set(key, value, ttl);
            _keys[key] = true;
        }

        public T? Get<T>(string key)
        {
            if (_cache.TryGetValue(key, out T value))
                return value;

            _keys.TryRemove(key, out _);
            return default;
        }

        public void PrintCache()
        {
            Console.WriteLine("---- Memory Cache Contents ----");

            foreach (var key in _keys.Keys)
            {
                if (!_cache.TryGetValue(key, out var value))
                    continue;

                Console.WriteLine($"Key: {key}");

                if (value is Array arr)
                {
                    Console.WriteLine($"Value: Array ({arr.Length} items)");

                    foreach (var item in arr)
                    {
                        Console.WriteLine($"  - {item}");
                    }
                }
                else
                {
                    Console.WriteLine($"Value: {value}");
                }

                Console.WriteLine();
            }                   

            Console.WriteLine("--------------------------------");
        }

    }
}