namespace WMINDEdgeGateway.Infrastructure.Services
{
    public class GatewayOptions
    {
        public string ClientId { get; set; } = "";
        public string ClientSecret { get; set; } = "";
    }

    public class CacheOptions
    {
        public int ConfigurationsMinutes { get; set; } = 30;
    }
}
