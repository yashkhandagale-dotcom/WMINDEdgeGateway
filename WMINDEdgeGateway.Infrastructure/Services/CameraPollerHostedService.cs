using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using WMINDEdgeGateway.Application.DTOs;
using WMINDEdgeGateway.Application.Interfaces;
using WMINDEdgeGateway.Infrastructure.Caching;

namespace WMINDEdgeGateway.Infrastructure.Services
{
    public class CameraPollerHostedService : BackgroundService
    {
        private readonly ILogger<CameraPollerHostedService> _logger;
        private readonly IS3UploaderService _s3Uploader;
        private readonly IMemoryCacheService _cache;

        private readonly Channel<(byte[], FrameUploadMetadata)> _uploadChannel =
            Channel.CreateBounded<(byte[], FrameUploadMetadata)>(new BoundedChannelOptions(200)
            {
                FullMode = BoundedChannelFullMode.DropWrite
            });

        private readonly List<Task> _uploadWorkers = new();

        //  Dynamic worker control
        private int _currentWorkerCount = 0;
        private readonly int _minWorkers = 3;
        private readonly int _maxWorkers = 10;

        public CameraPollerHostedService(
            ILogger<CameraPollerHostedService> logger,
            IS3UploaderService s3Uploader,
            IMemoryCacheService cache)
        {
            _logger = logger;
            _s3Uploader = s3Uploader;
            _cache = cache;
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            List<DeviceConfigurationDto>? cameraDevices = null;

            // Wait for cache
            while ((cameraDevices = _cache.Get<List<DeviceConfigurationDto>>("CameraDevices")) == null)
            {
                _logger.LogWarning("CameraDevices not found in cache. Retrying...");
                await Task.Delay(2000, ct);
            }

            _logger.LogInformation("Found {Count} camera device(s)", cameraDevices.Count);

            //  Start minimum workers
            for (int i = 0; i < _minWorkers; i++)
            {
                StartNewWorker(ct);
            }

            //  Start dynamic scaler
            _ = Task.Run(() => ScaleWorkers(ct), ct);

            // Start camera loops
            var cameraTasks = cameraDevices
                .Where(d => d.Cameras != null && d.Cameras.Count > 0)
                .SelectMany(device =>
                    device.Cameras!.Select(cam =>
                        Task.Run(() => SafeCameraLoop(device, cam, ct), ct)))
                .ToList();

            await Task.WhenAll(cameraTasks);

            // Shutdown
            _uploadChannel.Writer.Complete();
            await Task.WhenAll(_uploadWorkers);
        }

        // ────────────────────────────────────────────────
        private void StartNewWorker(CancellationToken ct)
        {
            Interlocked.Increment(ref _currentWorkerCount);

            var task = Task.Run(async () =>
            {
                _logger.LogInformation("Worker started. Total workers: {Count}", _currentWorkerCount);

                try
                {
                    await UploadWorker(ct);
                }
                finally
                {
                    Interlocked.Decrement(ref _currentWorkerCount);
                    _logger.LogInformation("Worker stopped. Total workers: {Count}", _currentWorkerCount);
                }
            }, ct);

            _uploadWorkers.Add(task);
        }

        // ────────────────────────────────────────────────
        private async Task ScaleWorkers(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(2000, ct);

                int queueSize = _uploadChannel.Reader.Count;

                _logger.LogInformation("Queue size: {Queue}, Workers: {Workers}", queueSize, _currentWorkerCount);

                // Scale UP
                if (queueSize > 120 && _currentWorkerCount < _maxWorkers)
                {
                    _logger.LogWarning("High queue detected. Scaling UP workers...");
                    StartNewWorker(ct);
                }

                //  No scale down (intentional for stability)
            }
        }

        // ────────────────────────────────────────────────
        private async Task SafeCameraLoop(
            DeviceConfigurationDto device,
            CameraConfigDto cam,
            CancellationToken ct)
        {
            try
            {
                await PollCameraAsync(device, cam, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Camera crashed: {CamId}", cam.CamId);
            }
        }

        // ────────────────────────────────────────────────
        private async Task PollCameraAsync(
            DeviceConfigurationDto device,
            CameraConfigDto cam,
            CancellationToken ct)
        {
            var fps = Math.Clamp(cam.Fps, 1, 30);
            var intervalMs = 1000 / fps;

            _logger.LogInformation("Starting cam={CamId} at {Fps} FPS", cam.CamId, fps);

            VideoCapture? capture = null;
            int retryDelay = 2000;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (capture == null || !capture.IsOpened())
                    {
                        capture?.Dispose();

                        capture = string.IsNullOrEmpty(cam.StreamUrl)
                            ? new VideoCapture(cam.DeviceIndex ?? 0)
                            : new VideoCapture(cam.StreamUrl);

                        if (!capture.IsOpened())
                        {
                            _logger.LogWarning("Cannot open cam={CamId}, retrying...", cam.CamId);
                            await Task.Delay(retryDelay, ct);
                            retryDelay = Math.Min(retryDelay * 2, 30000);
                            continue;
                        }

                        retryDelay = 2000;
                        _logger.LogInformation("Stream opened cam={CamId}", cam.CamId);
                    }

                    var sw = Stopwatch.StartNew();

                    using var frame = new Mat();

                    if (!capture.Read(frame) || frame.Empty())
                    {
                        _logger.LogWarning("Empty frame cam={CamId}", cam.CamId);
                        capture.Release();
                        capture = null;
                        await Task.Delay(2000, ct);
                        continue;
                    }

                    Cv2.ImEncode(".jpg", frame, out var jpegBytes,
                        new[] { new ImageEncodingParam(ImwriteFlags.JpegQuality, 85) });

                    if (jpegBytes == null || jpegBytes.Length == 0)
                        continue;

                    var metadata = new FrameUploadMetadata
                    {
                        CamId = cam.CamId,
                        DeviceId = device.Id.ToString(),
                        ProductId = device.ProductId ?? "unknown",
                        CameraName = cam.Name,
                        CapturedAt = DateTime.UtcNow
                    };

                    // enqueue or drop
                    if (!_uploadChannel.Writer.TryWrite((jpegBytes, metadata)))
                    {
                        _logger.LogWarning("Queue full → dropping frame cam={CamId}", cam.CamId);
                    }

                    var delay = intervalMs - (int)sw.ElapsedMilliseconds;
                    if (delay > 0)
                        await Task.Delay(delay, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Camera error cam={CamId}", cam.CamId);
                    await Task.Delay(3000, ct);
                }
            }

            capture?.Dispose();
            _logger.LogInformation("Camera stopped cam={CamId}", cam.CamId);
        }

        // ────────────────────────────────────────────────
        private async Task UploadWorker(CancellationToken ct)
        {
            await foreach (var (bytes, metadata) in _uploadChannel.Reader.ReadAllAsync(ct))
            {
                try
                {
                    await _s3Uploader.UploadFrameAsync(bytes, metadata, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Upload failed cam={CamId}", metadata.CamId);
                }
            }
        }
    }
}