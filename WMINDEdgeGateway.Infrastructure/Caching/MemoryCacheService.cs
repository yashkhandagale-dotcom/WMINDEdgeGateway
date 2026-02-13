using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;

namespace WMINDEdgeGateway.Infrastructure.Caching
{
    public interface IMemoryCacheService
    {
        void Set<T>(string key, T value, TimeSpan duration);
        T? Get<T>(string key);
        void PrintCache();
    }

    public class MemoryCacheService : IMemoryCacheService
    {
        private readonly ConcurrentDictionary<string, (object Value, DateTime Expiry)> _cache = new();

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true
        };

        public void Set<T>(string key, T value, TimeSpan duration)
        {
            _cache[key] = (value!, DateTime.UtcNow.Add(duration));
        }

        public T? Get<T>(string key)
        {
            if (_cache.TryGetValue(key, out var entry))
            {
                if (entry.Expiry > DateTime.UtcNow)
                    return (T)entry.Value;
                _cache.TryRemove(key, out _);
            }
            return default;
        }

        public void PrintCache()
        {
            Console.WriteLine("---- Memory Cache Contents ----");

            foreach (var key in _cache.Keys)
            {
                if (!_cache.TryGetValue(key, out var entry) || entry.Expiry <= DateTime.UtcNow)
                    continue;

                Console.WriteLine($"Key      : {key}");
                Console.WriteLine($"Expires  : {entry.Expiry:yyyy-MM-dd HH:mm:ss} UTC");

                if (entry.Value is Array arr)
                {
                    Console.WriteLine($"Count    : {arr.Length} items");
                    Console.WriteLine();

                    int i = 1;
                    foreach (var item in arr)
                    {
                        Console.WriteLine($"  [{i++}] {JsonSerializer.Serialize(item, _jsonOptions)}");
                        Console.WriteLine();
                    }
                }
                else
                {
                    Console.WriteLine($"Value    : {JsonSerializer.Serialize(entry.Value, _jsonOptions)}");
                }

                Console.WriteLine("--------------------------------");
            }
        }
    }
}