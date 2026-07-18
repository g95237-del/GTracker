using OpenCvSharp;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace GTracker.App.Capture;

public sealed class DxgiWindowCapture : IDisposable
{
    private readonly nint _windowHandle;
    private readonly OutputDescription _outputDescription;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGIOutputDuplication _duplication;
    private ID3D11Texture2D? _stagingTexture;
    private int _stagingWidth;
    private int _stagingHeight;
    private Format _stagingFormat;
    private bool _disposed;

    public DxgiWindowCapture(nint windowHandle)
    {
        _windowHandle = windowHandle;
        var monitor = WindowCatalog.MonitorForWindow(windowHandle);
        using var factory = CreateDXGIFactory1<IDXGIFactory1>();

        IDXGIAdapter1? selectedAdapter = null;
        IDXGIOutput? selectedOutput = null;
        for (uint adapterIndex = 0; factory.EnumAdapters1(adapterIndex, out var adapter).Success; adapterIndex++)
        {
            for (uint outputIndex = 0; adapter.EnumOutputs(outputIndex, out var output).Success; outputIndex++)
            {
                if (output.Description.Monitor == monitor)
                {
                    selectedAdapter = adapter;
                    selectedOutput = output;
                    break;
                }

                output.Dispose();
            }

            if (selectedAdapter is not null)
            {
                break;
            }

            adapter.Dispose();
        }

        if (selectedAdapter is null || selectedOutput is null)
        {
            throw new InvalidOperationException("Could not find the DXGI output containing the selected window.");
        }

        try
        {
            _outputDescription = selectedOutput.Description;
            if (_outputDescription.Rotation != ModeRotation.Identity)
            {
                throw new NotSupportedException("Rotated displays are not supported by the current DXGI capture backend.");
            }
            var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1 };
            D3D11CreateDevice(selectedAdapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, levels,
                out _device, out _context).CheckError();
            using var output1 = selectedOutput.QueryInterface<IDXGIOutput1>();
            _duplication = output1.DuplicateOutput(_device);
        }
        finally
        {
            selectedOutput.Dispose();
            selectedAdapter.Dispose();
        }
    }

    public unsafe Mat? TryCapture(int timeoutMilliseconds = 8)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (WindowCatalog.MonitorForWindow(_windowHandle) != _outputDescription.Monitor)
        {
            throw new InvalidOperationException("The captured window moved to another monitor. Restart capture to rebuild Desktop Duplication.");
        }
        if (!WindowCatalog.TryGetClientScreenRect(_windowHandle, out var windowRect))
        {
            return null;
        }

        var desktop = _outputDescription.DesktopCoordinates;
        var left = Math.Clamp(windowRect.Left - desktop.Left, 0, desktop.Right - desktop.Left);
        var top = Math.Clamp(windowRect.Top - desktop.Top, 0, desktop.Bottom - desktop.Top);
        var right = Math.Clamp(windowRect.Right - desktop.Left, left, desktop.Right - desktop.Left);
        var bottom = Math.Clamp(windowRect.Bottom - desktop.Top, top, desktop.Bottom - desktop.Top);
        var width = right - left;
        var height = bottom - top;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var acquired = false;
        IDXGIResource? desktopResource = null;
        try
        {
            var result = _duplication.AcquireNextFrame((uint)Math.Max(0, timeoutMilliseconds), out _, out desktopResource);
            if (result.Failure)
            {
                if (result.Code == unchecked((int)0x887A0027))
                {
                    return null;
                }
                throw new InvalidOperationException($"DXGI Desktop Duplication failed with 0x{result.Code:X8}.");
            }

            acquired = true;
            using var source = desktopResource.QueryInterface<ID3D11Texture2D>();
            if (source.Description.Format != Format.B8G8R8A8_UNorm)
            {
                throw new NotSupportedException($"Desktop format {source.Description.Format} is not supported. Disable HDR/10-bit output for this capture session.");
            }
            EnsureStagingTexture(width, height, source.Description.Format);
            var sourceBox = new Box(left, top, 0, right, bottom, 1);
            _context.CopySubresourceRegion(_stagingTexture!, 0, 0, 0, 0, source, 0, sourceBox);
            _context.Map(_stagingTexture!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mapped).CheckError();
            try
            {
                var frame = new Mat(height, width, MatType.CV_8UC4);
                var bytesPerRow = width * 4L;
                for (var row = 0; row < height; row++)
                {
                    Buffer.MemoryCopy(
                        (void*)(mapped.DataPointer + row * mapped.RowPitch),
                        (void*)(frame.Data + row * frame.Step()),
                        bytesPerRow,
                        bytesPerRow);
                }

                return frame;
            }
            finally
            {
                _context.Unmap(_stagingTexture!, 0);
            }
        }
        finally
        {
            desktopResource?.Dispose();
            if (acquired)
            {
                _duplication.ReleaseFrame();
            }
        }
    }

    private void EnsureStagingTexture(int width, int height, Format format)
    {
        if (_stagingTexture is not null && width == _stagingWidth && height == _stagingHeight && format == _stagingFormat)
        {
            return;
        }

        _stagingTexture?.Dispose();
        var description = new Texture2DDescription(format, (uint)width, (uint)height, 1, 1,
            BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read, 1, 0);
        _stagingTexture = _device.CreateTexture2D(description);
        _stagingWidth = width;
        _stagingHeight = height;
        _stagingFormat = format;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stagingTexture?.Dispose();
        _duplication.Dispose();
        _context.Dispose();
        _device.Dispose();
    }
}
