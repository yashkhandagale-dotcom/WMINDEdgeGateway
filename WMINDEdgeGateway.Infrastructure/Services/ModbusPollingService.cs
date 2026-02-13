using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.ComponentModel;
using WMINDEdgeGateway.Infrastructure.Caching;
using WMINDEdgeGateway.Application.DTOs;


namespace WMINDEdgeGateway.Infrastructure.Services
{
    public class ModbusPollingService : BackgroundService
    {
        public readonly MemoryCacheService _cache;

         public ModbusPollingService(MemoryCacheService cache)
        {
            _cache = cache;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                Console.Write("Config recevied: Poller Started");
                var Configs = _cache.Get<List<DeviceConfigurationDto>>("DeviceConfigurations");

                Console.WriteLine(Configs);

                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }

        }
    }
}
