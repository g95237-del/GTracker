using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GTracker.Core.Projects;

namespace GTracker.App.Controls;

public sealed class LinearSimulatorOverlay : FrameworkElement
{
    private static readonly Brush BodyBrush = Freeze(new SolidColorBrush(Color.FromArgb(205, 15, 23, 42)));
    private static readonly Brush TrackBrush = Freeze(new SolidColorBrush(Color.FromRgb(71, 85, 105)));
    private static readonly Brush ActiveTrackBrush = Freeze(new SolidColorBrush(Color.FromRgb(20, 184, 166)));
    private static readonly Brush MarkerBrush = Freeze(new SolidColorBrush(Color.FromRgb(251, 113, 133)));
    private static readonly Brush HandleBrush = Freeze(new SolidColorBrush(Color.FromRgb(241, 245, 249)));
    private static readonly Brush TextBrush = Freeze(new SolidColorBrush(Color.FromRgb(241, 245, 249)));
    private static readonly Pen BorderPen = Freeze(new Pen(ActiveTrackBrush, 1.5));
    private static readonly Pen TrackPen = Freeze(new Pen(TrackBrush, 5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    private static readonly Pen ActiveTrackPen = Freeze(new Pen(ActiveTrackBrush, 5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    private static readonly Pen MarkerPen = Freeze(new Pen(TextBrush, 2));
    private LinearSimulatorLayout _layout = new();
    private LinearSimulatorLayout? _dragStartLayout;
    private Point _dragStartPoint;
    private DragMode _dragMode;

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(LinearSimulatorOverlay),
        new FrameworkPropertyMetadata(50d, FrameworkPropertyMetadataOptions.AffectsRender));

    public ImageSource? ImageSource
    {
        get => (ImageSource?)GetValue(ImageSourceProperty);
        set => SetValue(ImageSourceProperty, value);
    }

    public static readonly DependencyProperty ImageSourceProperty = DependencyProperty.Register(
        nameof(ImageSource), typeof(ImageSource), typeof(LinearSimulatorOverlay),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public event EventHandler? LayoutChanged;

    public void ApplyLayout(LinearSimulatorLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        _layout = Sanitize(layout);
        InvalidateVisual();
    }

    public LinearSimulatorLayout GetLayout(bool isVisible) => new()
    {
        IsVisible = isVisible,
        CenterX = _layout.CenterX,
        CenterY = _layout.CenterY,
        Width = _layout.Width,
        Height = _layout.Height,
        RotationDegrees = _layout.RotationDegrees
    };

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        if (ActualWidth < 2 || ActualHeight < 2) return;

        var geometry = GetGeometry(_layout);
        context.PushTransform(new RotateTransform(_layout.RotationDegrees, geometry.Center.X, geometry.Center.Y));
        context.DrawRoundedRectangle(BodyBrush, BorderPen, geometry.Body, 7, 7);

        var trackStart = new Point(geometry.Body.Left + 16, geometry.Center.Y);
        var trackEnd = new Point(geometry.Body.Right - 16, geometry.Center.Y);
        var amount = Math.Clamp(Value, 0, 100) / 100d;
        var marker = new Point(trackStart.X + (trackEnd.X - trackStart.X) * amount, geometry.Center.Y);
        context.DrawLine(TrackPen, trackStart, trackEnd);
        context.DrawLine(ActiveTrackPen, trackStart, marker);
        context.DrawEllipse(MarkerBrush, MarkerPen, marker, 8, 8);

        DrawText(context, "0", new Point(geometry.Body.Left + 8, geometry.Body.Bottom - 18), 10, TextBrush);
        DrawText(context, "100", new Point(geometry.Body.Right - 28, geometry.Body.Bottom - 18), 10, TextBrush);
        var valueText = Math.Round(Math.Clamp(Value, 0, 100)).ToString(CultureInfo.InvariantCulture);
        DrawText(context, valueText, new Point(marker.X - valueText.Length * 3, geometry.Body.Top + 4), 11, TextBrush);

        DrawHandles(context, geometry);
        context.Pop();
    }

    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) =>
        FindDragMode(hitTestParameters.HitPoint) == DragMode.None
            ? null
            : new PointHitTestResult(this, hitTestParameters.HitPoint);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var mode = FindDragMode(e.GetPosition(this));
        if (mode == DragMode.None) return;
        _dragMode = mode;
        _dragStartPoint = e.GetPosition(this);
        _dragStartLayout = Clone(_layout);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var position = e.GetPosition(this);
        if (_dragMode != DragMode.None && e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateDrag(position);
            e.Handled = true;
            return;
        }
        Cursor = CursorFor(FindDragMode(position));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragMode == DragMode.None) return;
        UpdateDrag(e.GetPosition(this));
        EndDrag();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragMode != DragMode.None) EndDrag();
    }

    private void UpdateDrag(Point position)
    {
        if (_dragStartLayout is null) return;
        var viewport = GetViewport();
        if (viewport.Width <= 0 || viewport.Height <= 0) return;
        var initial = GetGeometry(_dragStartLayout);

        if (_dragMode == DragMode.Move)
        {
            _layout.CenterX = Math.Clamp((initial.Center.X + position.X - _dragStartPoint.X - viewport.Left) / viewport.Width, 0.02, 0.98);
            _layout.CenterY = Math.Clamp((initial.Center.Y + position.Y - _dragStartPoint.Y - viewport.Top) / viewport.Height, 0.02, 0.98);
        }
        else if (_dragMode == DragMode.Rotate)
        {
            var angle = Math.Atan2(position.Y - initial.Center.Y, position.X - initial.Center.X) * 180 / Math.PI + 90;
            _layout.RotationDegrees = NormalizeAngle(angle);
        }
        else
        {
            Resize(position, viewport, initial);
        }

        InvalidateVisual();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Resize(Point position, Rect viewport, SimulatorGeometry initial)
    {
        var delta = position - _dragStartPoint;
        var radians = -_dragStartLayout!.RotationDegrees * Math.PI / 180;
        var localX = delta.X * Math.Cos(radians) - delta.Y * Math.Sin(radians);
        var localY = delta.X * Math.Sin(radians) + delta.Y * Math.Cos(radians);
        var resizeLeft = _dragMode is DragMode.Left or DragMode.TopLeft or DragMode.BottomLeft;
        var resizeRight = _dragMode is DragMode.Right or DragMode.TopRight or DragMode.BottomRight;
        var resizeTop = _dragMode is DragMode.Top or DragMode.TopLeft or DragMode.TopRight;
        var resizeBottom = _dragMode is DragMode.Bottom or DragMode.BottomLeft or DragMode.BottomRight;
        var minWidth = Math.Min(100, viewport.Width);
        var maxWidth = Math.Max(minWidth, viewport.Width * 0.95);
        var minHeight = Math.Min(42, viewport.Height);
        var maxHeight = Math.Max(minHeight, Math.Min(180, viewport.Height * 0.45));
        var width = initial.Width;
        var height = initial.Height;
        var centerShiftX = 0d;
        var centerShiftY = 0d;

        if (resizeLeft)
        {
            width = Math.Clamp(initial.Width - localX, minWidth, maxWidth);
            centerShiftX = (initial.Width - width) / 2;
        }
        else if (resizeRight)
        {
            width = Math.Clamp(initial.Width + localX, minWidth, maxWidth);
            centerShiftX = (width - initial.Width) / 2;
        }
        if (resizeTop)
        {
            height = Math.Clamp(initial.Height - localY, minHeight, maxHeight);
            centerShiftY = (initial.Height - height) / 2;
        }
        else if (resizeBottom)
        {
            height = Math.Clamp(initial.Height + localY, minHeight, maxHeight);
            centerShiftY = (height - initial.Height) / 2;
        }

        var angle = _dragStartLayout.RotationDegrees * Math.PI / 180;
        var worldShiftX = centerShiftX * Math.Cos(angle) - centerShiftY * Math.Sin(angle);
        var worldShiftY = centerShiftX * Math.Sin(angle) + centerShiftY * Math.Cos(angle);
        _layout.CenterX = Math.Clamp((initial.Center.X + worldShiftX - viewport.Left) / viewport.Width, 0.02, 0.98);
        _layout.CenterY = Math.Clamp((initial.Center.Y + worldShiftY - viewport.Top) / viewport.Height, 0.02, 0.98);
        _layout.Width = width / viewport.Width;
        _layout.Height = height / viewport.Height;
    }

    private void EndDrag()
    {
        _dragMode = DragMode.None;
        _dragStartLayout = null;
        Cursor = Cursors.Arrow;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    private DragMode FindDragMode(Point point)
    {
        if (ActualWidth < 2 || ActualHeight < 2) return DragMode.None;
        var geometry = GetGeometry(_layout);
        foreach (var handle in GetHandlePoints(geometry))
        {
            if ((handle.Value - point).LengthSquared <= 100) return handle.Key;
        }

        var local = RotateAround(point, geometry.Center, -_layout.RotationDegrees);
        var body = geometry.Body;
        body.Inflate(5, 5);
        return body.Contains(local) ? DragMode.Move : DragMode.None;
    }

    private void DrawHandles(DrawingContext context, SimulatorGeometry geometry)
    {
        var body = geometry.Body;
        var localHandles = new[]
        {
            new Point(body.Left, body.Top), new Point(body.Left + body.Width / 2, body.Top), new Point(body.Right, body.Top),
            new Point(body.Left, body.Top + body.Height / 2), new Point(body.Right, body.Top + body.Height / 2),
            new Point(body.Left, body.Bottom), new Point(body.Left + body.Width / 2, body.Bottom), new Point(body.Right, body.Bottom)
        };
        foreach (var handle in localHandles) context.DrawRectangle(HandleBrush, BorderPen, new Rect(handle.X - 4, handle.Y - 4, 8, 8));
        var rotation = new Point(geometry.Center.X, body.Top - 25);
        context.DrawLine(BorderPen, new Point(geometry.Center.X, body.Top), rotation);
        context.DrawEllipse(HandleBrush, BorderPen, rotation, 5, 5);
    }

    private Dictionary<DragMode, Point> GetHandlePoints(SimulatorGeometry geometry)
    {
        var body = geometry.Body;
        var local = new Dictionary<DragMode, Point>
        {
            [DragMode.TopLeft] = new(body.Left, body.Top),
            [DragMode.Top] = new(geometry.Center.X, body.Top),
            [DragMode.TopRight] = new(body.Right, body.Top),
            [DragMode.Left] = new(body.Left, geometry.Center.Y),
            [DragMode.Right] = new(body.Right, geometry.Center.Y),
            [DragMode.BottomLeft] = new(body.Left, body.Bottom),
            [DragMode.Bottom] = new(geometry.Center.X, body.Bottom),
            [DragMode.BottomRight] = new(body.Right, body.Bottom),
            [DragMode.Rotate] = new(geometry.Center.X, body.Top - 25)
        };
        return local.ToDictionary(pair => pair.Key, pair => RotateAround(pair.Value, geometry.Center, _layout.RotationDegrees));
    }

    private SimulatorGeometry GetGeometry(LinearSimulatorLayout layout)
    {
        var viewport = GetViewport();
        var center = new Point(viewport.Left + layout.CenterX * viewport.Width, viewport.Top + layout.CenterY * viewport.Height);
        var minWidth = Math.Min(100, viewport.Width);
        var maxWidth = Math.Max(minWidth, viewport.Width * 0.95);
        var minHeight = Math.Min(42, viewport.Height);
        var maxHeight = Math.Max(minHeight, Math.Min(180, viewport.Height * 0.45));
        var width = Math.Clamp(layout.Width * viewport.Width, minWidth, maxWidth);
        var height = Math.Clamp(layout.Height * viewport.Height, minHeight, maxHeight);
        return new(viewport, center, width, height, new Rect(center.X - width / 2, center.Y - height / 2, width, height));
    }

    private Rect GetViewport()
    {
        if (ImageSource is not BitmapSource { PixelWidth: > 0, PixelHeight: > 0 } bitmap)
            return new Rect(0, 0, Math.Max(0, ActualWidth), Math.Max(0, ActualHeight));
        var scale = Math.Min(ActualWidth / bitmap.PixelWidth, ActualHeight / bitmap.PixelHeight);
        var width = bitmap.PixelWidth * scale;
        var height = bitmap.PixelHeight * scale;
        return new Rect((ActualWidth - width) / 2, (ActualHeight - height) / 2, width, height);
    }

    private static Point RotateAround(Point point, Point center, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        var x = point.X - center.X;
        var y = point.Y - center.Y;
        return new Point(center.X + x * Math.Cos(radians) - y * Math.Sin(radians),
            center.Y + x * Math.Sin(radians) + y * Math.Cos(radians));
    }

    private static Cursor CursorFor(DragMode mode) => mode switch
    {
        DragMode.Move => Cursors.SizeAll,
        DragMode.Left or DragMode.Right => Cursors.SizeWE,
        DragMode.Top or DragMode.Bottom => Cursors.SizeNS,
        DragMode.TopLeft or DragMode.BottomRight => Cursors.SizeNWSE,
        DragMode.TopRight or DragMode.BottomLeft => Cursors.SizeNESW,
        DragMode.Rotate => Cursors.Hand,
        _ => Cursors.Arrow
    };

    private static LinearSimulatorLayout Sanitize(LinearSimulatorLayout layout) => new()
    {
        IsVisible = layout.IsVisible,
        CenterX = Math.Clamp(double.IsFinite(layout.CenterX) ? layout.CenterX : 0.5, 0.02, 0.98),
        CenterY = Math.Clamp(double.IsFinite(layout.CenterY) ? layout.CenterY : 0.38, 0.02, 0.98),
        Width = Math.Clamp(double.IsFinite(layout.Width) ? layout.Width : 0.42, 0.05, 0.95),
        Height = Math.Clamp(double.IsFinite(layout.Height) ? layout.Height : 0.1, 0.03, 0.45),
        RotationDegrees = NormalizeAngle(double.IsFinite(layout.RotationDegrees) ? layout.RotationDegrees : -12)
    };

    private static LinearSimulatorLayout Clone(LinearSimulatorLayout layout) => new()
    {
        IsVisible = layout.IsVisible,
        CenterX = layout.CenterX,
        CenterY = layout.CenterY,
        Width = layout.Width,
        Height = layout.Height,
        RotationDegrees = layout.RotationDegrees
    };

    private static double NormalizeAngle(double angle)
    {
        angle %= 360;
        if (angle > 180) angle -= 360;
        if (angle <= -180) angle += 360;
        return angle;
    }

    private void DrawText(DrawingContext context, string text, Point point, double size, Brush brush)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        context.DrawText(formatted, point);
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private readonly record struct SimulatorGeometry(Rect Viewport, Point Center, double Width, double Height, Rect Body);

    private enum DragMode
    {
        None,
        Move,
        Left,
        Right,
        Top,
        Bottom,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight,
        Rotate
    }
}
