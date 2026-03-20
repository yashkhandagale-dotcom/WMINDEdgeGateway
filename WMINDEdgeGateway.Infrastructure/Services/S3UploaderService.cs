using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using WMINDEdgeGateway.Application.Interfaces;

namespace WMINDEdgeGateway.Infrastructure.Services
{
    public class S3UploaderService : IS3UploaderService
    {
        private readonly IAmazonS3 _s3Client;
        private readonly ILogger<S3UploaderService> _logger;
        private readonly string _bucketName;

        // S3 key pattern:  frames/{productId}/{camId}/{yyyy-MM-dd}/{timestamp}.jpg
        private const string KeyTemplate = "frames/{0}/{1}/{2}/{3}.jpg";

        public S3UploaderService(
            IAmazonS3 s3Client,
            ILogger<S3UploaderService> logger,
            string bucketName)
        {
            _s3Client  = s3Client  ?? throw new ArgumentNullException(nameof(s3Client));
            _logger    = logger    ?? throw new ArgumentNullException(nameof(logger));
            _bucketName = bucketName;
        }

        public async Task<string> UploadFrameAsync(
            byte[] frameBytes,
            FrameUploadMetadata metadata,
            CancellationToken ct = default)
        {
            // ── Build S3 key ──────────────────────────────────────────────────
            var dateFolder = metadata.CapturedAt.ToString("yyyy-MM-dd");
            var timestamp  = metadata.CapturedAt.ToString("yyyyMMdd_HHmmss_fff");

            var s3Key = string.Format(
                KeyTemplate,
                metadata.ProductId,
                metadata.CamId,
                dateFolder,
                timestamp);

            // ── Build request with metadata tags ──────────────────────────────
            using var stream = new MemoryStream(frameBytes);

            var request = new PutObjectRequest
            {
                BucketName  = _bucketName,
                Key         = s3Key,
                InputStream = stream,
                ContentType = "image/jpeg",

                // S3 object metadata — visible on cloud side when fetching
                Metadata =
                {
                    ["cam-id"]       = metadata.CamId,
                    ["device-id"]    = metadata.DeviceId,
                    ["product-id"]   = metadata.ProductId,
                    ["camera-name"]  = metadata.CameraName,
                    ["captured-at"]  = metadata.CapturedAt.ToString("o"),  // ISO-8601
                }
            };

            // Tagging for S3 lifecycle / filtering (separate from metadata)
            request.TagSet.Add(new Tag { Key = "product-id", Value = metadata.ProductId });
            request.TagSet.Add(new Tag { Key = "cam-id",     Value = metadata.CamId });

            try
            {
                await _s3Client.PutObjectAsync(request, ct);
                _logger.LogDebug(
                    "Frame uploaded → s3://{Bucket}/{Key}  [cam={CamId}, product={ProductId}]",
                    _bucketName, s3Key, metadata.CamId, metadata.ProductId);

                return s3Key;
            }
            catch (AmazonS3Exception ex)
            {
                _logger.LogError(ex,
                    "S3 upload failed for cam={CamId}, product={ProductId}: {Msg}",
                    metadata.CamId, metadata.ProductId, ex.Message);
                throw;
            }
        }
    }
}