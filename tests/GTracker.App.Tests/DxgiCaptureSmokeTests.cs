using System.Diagnostics;
using GTracker.App.Capture;
using OpenCvSharp;

namespace GTracker.App.Tests;

public sealed class DxgiCaptureSmokeTests
{
    [Fact]
    [Trait("Category", "HardwareSmoke")]
    public void Capture_RunningStudioWindowProducesPixelsWhenAvailable()
    {
        var process = Process.GetProcessesByName("GTracker.App").FirstOrDefault();
        if (process is null)
        {
            return;
        }

        var window = WindowCatalog.GetWindows().FirstOrDefault(item => item.ProcessId == process.Id);
        Assert.NotNull(window);
        using var capture = new DxgiWindowCapture(window.Handle);
        Mat? frame = null;
        try
        {
            for (var attempt = 0; attempt < 120 && frame is null; attempt++)
            {
                frame = capture.TryCapture(20);
                if (frame is null) Thread.Sleep(16);
            }

            Assert.NotNull(frame);
            Assert.True(frame.Width > 100);
            Assert.True(frame.Height > 100);
            Assert.Equal(4, frame.Channels());
        }
        finally
        {
            frame?.Dispose();
        }
    }
}
