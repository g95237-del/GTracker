using System.Diagnostics;
using OpenCvSharp;

namespace GTracker.App.Capture;

public sealed class CaptureSession : IAsyncDisposable
{
    private readonly DxgiWindowCapture _capture;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly int _recordingFps;
    private readonly CaptureCadence _cadence;
    private readonly object _resolvedSamplesGate = new();
    private readonly HashSet<long> _resolvedSamplesOutOfOrder = [];
    private Task? _captureTask;
    private Task? _encoderTask;
    private EncodeFrame? _pendingFrame;
    private long _capturedFrames;
    private long _sampledFrames;
    private long _encodedFrames;
    private long _droppedBeforeEncodeFrames;
    private long _captureAttemptedThroughUtcTicks;
    private long _sampledThroughCaptureAttempt;
    private long _resolvedThroughSample;
    private int _faultRaised;

    public CaptureSession(nint windowHandle, TimeSpan retention, int recordingFps = 30, long maximumBytes = 512L * 1024 * 1024)
    {
        _capture = new DxgiWindowCapture(windowHandle);
        _recordingFps = Math.Clamp(recordingFps, 5, 30);
        _cadence = new CaptureCadence(_recordingFps);
        Buffer = new RollingJpegBuffer(retention, maximumBytes);
    }

    public RollingJpegBuffer Buffer { get; }
    public int RecordingFps => _recordingFps;
    public long CapturedFrames => Interlocked.Read(ref _capturedFrames);
    public long SampledFrames => Interlocked.Read(ref _sampledFrames);
    public long EncodedFrames => Interlocked.Read(ref _encodedFrames);
    public long DroppedBeforeEncodeFrames => Interlocked.Read(ref _droppedBeforeEncodeFrames);
    public event EventHandler<JpegFrame>? FrameEncoded;
    public event EventHandler<Exception>? Faulted;

    public void Start()
    {
        if (_captureTask is not null) throw new InvalidOperationException("Capture session is already started.");
        _captureTask = Task.Run(() => CaptureLoopAsync(_shutdown.Token));
        _encoderTask = Task.Run(() => EncoderLoopAsync(_shutdown.Token));
    }

    private async Task CaptureLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1d / 60));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var frame = _capture.TryCapture(7);
                if (frame is null)
                {
                    MarkCaptureAttemptComplete();
                    continue;
                }

                Interlocked.Increment(ref _capturedFrames);
                var timestamp = Stopwatch.GetTimestamp();
                if (!_cadence.ShouldSample(timestamp))
                {
                    frame.Dispose();
                    MarkCaptureAttemptComplete();
                    continue;
                }

                var sampleSequence = Interlocked.Increment(ref _sampledFrames);
                var displaced = Interlocked.Exchange(ref _pendingFrame,
                    new(frame, timestamp, DateTimeOffset.UtcNow, sampleSequence));
                if (displaced is not null)
                {
                    Interlocked.Increment(ref _droppedBeforeEncodeFrames);
                    displaced.Frame.Dispose();
                    MarkSampleResolved(displaced.SampleSequence);
                }
                MarkCaptureAttemptComplete();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            RaiseFault(exception);
        }
    }

    private async Task EncoderLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var pending = Interlocked.Exchange(ref _pendingFrame, null);
                if (pending is null)
                {
                    await Task.Delay(2, cancellationToken);
                    continue;
                }

                using (pending.Frame)
                using (var bgr = new Mat())
                {
                    Cv2.CvtColor(pending.Frame, bgr, ColorConversionCodes.BGRA2BGR);
                    Cv2.ImEncode(".jpg", bgr, out var bytes,
                        new ImageEncodingParam(ImwriteFlags.JpegQuality, 82));
                    var encoded = new JpegFrame(pending.Timestamp, bytes, pending.Frame.Width, pending.Frame.Height, pending.CapturedAtUtc);
                    Buffer.Add(encoded);
                    Interlocked.Increment(ref _encodedFrames);
                    MarkSampleResolved(pending.SampleSequence);
                    FrameEncoded?.Invoke(this, encoded);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            RaiseFault(exception);
        }
    }

    private void RaiseFault(Exception exception)
    {
        if (Interlocked.Exchange(ref _faultRaised, 1) == 0)
        {
            _shutdown.Cancel();
            Faulted?.Invoke(this, exception);
        }
    }

    public async Task<bool> WaitForEncodedThroughAsync(
        DateTimeOffset capturedAtUtc,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var targetUtcTicks = capturedAtUtc.UtcDateTime.Ticks;
        long? requiredSampledFrames = null;
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout && !_shutdown.IsCancellationRequested)
        {
            if (HasResolvedCaptureThrough(targetUtcTicks, ref requiredSampledFrames)) return true;
            await Task.Delay(5, cancellationToken);
        }
        return HasResolvedCaptureThrough(targetUtcTicks, ref requiredSampledFrames);
    }

    private bool HasResolvedCaptureThrough(long targetUtcTicks, ref long? requiredSampledFrames)
    {
        lock (_resolvedSamplesGate)
        {
            if (_captureAttemptedThroughUtcTicks < targetUtcTicks) return false;
            requiredSampledFrames ??= _sampledThroughCaptureAttempt;
            return _resolvedThroughSample >= requiredSampledFrames.Value;
        }
    }

    private void MarkSampleResolved(long sampleSequence)
    {
        lock (_resolvedSamplesGate)
        {
            if (sampleSequence <= _resolvedThroughSample) return;
            if (sampleSequence != _resolvedThroughSample + 1)
            {
                _resolvedSamplesOutOfOrder.Add(sampleSequence);
                return;
            }

            _resolvedThroughSample = sampleSequence;
            while (_resolvedSamplesOutOfOrder.Remove(_resolvedThroughSample + 1))
                _resolvedThroughSample++;
        }
    }

    private void MarkCaptureAttemptComplete()
    {
        lock (_resolvedSamplesGate)
        {
            _captureAttemptedThroughUtcTicks = DateTimeOffset.UtcNow.UtcDateTime.Ticks;
            _sampledThroughCaptureAttempt = SampledFrames;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        try
        {
            await Task.WhenAll(new[] { _captureTask, _encoderTask }.Where(task => task is not null).Cast<Task>());
        }
        catch (OperationCanceledException)
        {
        }
        Interlocked.Exchange(ref _pendingFrame, null)?.Frame.Dispose();
        _capture.Dispose();
        Buffer.Clear();
        _shutdown.Dispose();
    }

    private sealed record EncodeFrame(Mat Frame, long Timestamp, DateTimeOffset CapturedAtUtc, long SampleSequence);
}
