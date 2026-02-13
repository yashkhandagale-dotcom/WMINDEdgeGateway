using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WMINDEdgeGateway.Infrastructure.Services;

namespace WMINDEdgeGateway.Infrastructure.Extensions
{
    /// <summary>
    /// Extension methods for registering infrastructure services
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// Registers InfluxDB service and Modbus poller hosted service
        /// </summary>
        public static IServiceCollection AddModbusPollingServices(this IServiceCollection services, IConfiguration configuration)
        {
            // Register InfluxDB service as singleton
            services.AddSingleton<IInfluxDbService, InfluxDbService>();

            // Register Modbus poller as hosted service
            services.AddHostedService<ModbusPollerHostedService>();

            return services;
        }
    }
}