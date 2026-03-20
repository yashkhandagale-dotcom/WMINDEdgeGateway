namespace WMINDEdgeGateway.Application.Interfaces
{
    public class FrameUploadMetadata
    {
        public string CamId { get; set; } = string.Empty;
        public string DeviceId { get; set; } = string.Empty;
        public string ProductId { get; set; } = string.Empty;
        public string CameraName { get; set; } = string.Empty;
        public DateTime CapturedAt { get; set; }
        public string FrameKey { get; set; } = string.Empty;  // S3 object key
    }

    public interface IS3UploaderService
    {
        /// <summary>
        /// Uploads a raw JPEG frame to S3 and attaches metadata tags.
        /// </summary>
        Task<string> UploadFrameAsync(
            byte[] frameBytes,
            FrameUploadMetadata metadata,
            CancellationToken ct = default);
    }
}