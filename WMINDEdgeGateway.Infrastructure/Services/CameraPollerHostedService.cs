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

        //  Bounded queue (controls pressure)
        private readonly Channel<(byte[], FrameUploadMetadata)> _uploadChannel =
            Channel.CreateBounded<(byte[], FrameUploadMetadata)>(new BoundedChannelOptions(200)
            {
                FullMode = BoundedChannelFullMode.DropWrite
            });
            // the thread safe queue that will hold frames waiting to be uploaded. If the queue is full, new frames will be dropped to avoid memory issues.

            // 200 frames at 85% JPEG quality is roughly ~50-100MB of memory. This acts as a buffer during upload spikes, but prevents OOM crashes if S3 is slow or unavailable.

            // here camers is producer and uploader is consumer. If producer is faster than consumer, the queue will fill up and start dropping frames, which is a graceful degradation strategy.



        private readonly List<Task> _uploadWorkers = new();

        public CameraPollerHostedService(
            ILogger<CameraPollerHostedService> logger,
            IS3UploaderService s3Uploader,
            IMemoryCacheService cache)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _s3Uploader = s3Uploader ?? throw new ArgumentNullException(nameof(s3Uploader));
            _cache = cache;
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            List<DeviceConfigurationDto>? cameraDevices = null;

            // ── Wait for cache ─────────────────────────────
            while ((cameraDevices = _cache.Get<List<DeviceConfigurationDto>>("CameraDevices")) == null)
            {
                _logger.LogWarning("CameraDevices not found in cache. Retrying...");
                await Task.Delay(2000, ct);
            }

            _logger.LogInformation("Found {Count} camera device(s)", cameraDevices.Count);

            // ── Start upload workers (limited concurrency) ─
            int workerCount = 3;
            for (int i = 0; i < workerCount; i++)
            {
                _uploadWorkers.Add(Task.Run(() => UploadWorker(ct), ct));
            }

            // ── Start camera loops ─────────────────────────
            var cameraTasks = cameraDevices
                .Where(d => d.Cameras != null && d.Cameras.Count > 0)
                .SelectMany(device =>
                    device.Cameras!.Select(cam =>
                        Task.Run(() => SafeCameraLoop(device, cam, ct), ct)))
                .ToList();

            await Task.WhenAll(cameraTasks);

            // ── Shutdown upload workers ────────────────────
            _uploadChannel.Writer.Complete();
            // signal to upload workers that no more frames will be added. They will finish processing the existing queue and then exit.
            await Task.WhenAll(_uploadWorkers);
        }

        // ────────────────────────────────────────────────
        private async Task SafeCameraLoop(
            DeviceConfigurationDto device,
            CameraConfigDto cam,
            CancellationToken ct)
        {
            try
            {
                // each camera runs in its own loop. If it crashes (e.g. stream error), we catch the exception, log it, and the loop will exit gracefully without affecting other cameras or the main service.
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

            _logger.LogInformation(
                "Starting cam={CamId} ({Name}) at {Fps} FPS",
                cam.CamId, cam.Name, fps);

            VideoCapture? capture = null;
            int retryDelay = 2000;
            int frameCount = 0;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // ── (Re)open stream ─────────────────────
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

                    // ── Encode JPEG ─────────────────────────
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

                    //  enqueue or drop
                    if (!_uploadChannel.Writer.TryWrite((jpegBytes, metadata)))
                    {
                        _logger.LogWarning("Queue full → dropping frame cam={CamId}", cam.CamId);
                    }

                    // ── FPS control ─────────────────────────
                    var delay = intervalMs - (int)sw.ElapsedMilliseconds;
                    if (delay > 0)
                        await Task.Delay(delay, ct);

                    frameCount++;
                    if (frameCount % 100 == 0)
                    {
                        _logger.LogInformation("Cam={CamId} processed 100 frames", cam.CamId);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
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
                    // each worker continuously reads from the queue and uploads frames to S3. If an upload fails, we log the error and continue with the next frame, ensuring that one failed upload doesn't stop the worker.
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