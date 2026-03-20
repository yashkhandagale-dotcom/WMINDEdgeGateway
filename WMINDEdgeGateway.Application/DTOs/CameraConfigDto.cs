namespace WMINDEdgeGateway.Application.DTOs
{
    public class CameraConfigDto
    {
        public string CamId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int Fps { get; set; } = 5;
        public string StreamUrl { get; set; } = string.Empty;
        public int? DeviceIndex { get; set; } = null;
    }
}