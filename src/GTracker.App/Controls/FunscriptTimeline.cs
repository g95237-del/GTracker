using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using GTracker.Core.Projects;

namespace GTracker.App.Controls;

public sealed class FunscriptTimeline : FrameworkElement
{
    public const int TimeStepMilliseconds = 25;

    private static readonly Brush BackgroundBrush = Freeze(new SolidColorBrush(Color.FromRgb(3, 7, 18)));
    private static readonly Brush GridBrush = Freeze(new SolidColorBrush(Color.FromRgb(51, 65, 85)));
    private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromRgb(168, 180, 197)));
    private static readonly Brush CurveBrush = Freeze(new SolidColorBrush(Color.FromRgb(20, 184, 166)));
    private static readonly Brush PointBrush = Freeze(new SolidColorBrush(Color.FromRgb(241, 245, 249)));
    private static readonly Brush CursorBrush = Freeze(new SolidColorBrush(Color.FromRgb(251, 113, 133)));
    private static readonly Pen GridPen = Freeze(new Pen(GridBrush, 1));
    private static readonly Pen CurvePen = Freeze(new Pen(CurveBrush, 2));
    private static readonly Pen CursorPen = Freeze(new Pen(CursorBrush, 1.5));
    private readonly List<FunscriptPoint> _points = [];
    private readonly Stack<FunscriptPoint[]> _undoHistory = new();
    private readonly Stack<FunscriptPoint[]> _redoHistory = new();
    private FunscriptPoint[]? _dragStartPoints;
    private FunscriptPoint? _draggedPoint;
    private bool _draggingPoint;

    public bool IsReadOnly { get; set; }

    public int DurationMilliseconds
    {
        get => (int)GetValue(DurationMillisecondsProperty);
        set => SetValue(DurationMillisecondsProperty, value);
    }

    public static readonly DependencyProperty DurationMillisecondsProperty = DependencyProperty.Register(
        nameof(DurationMilliseconds), typeof(int), typeof(FunscriptTimeline),
        new FrameworkPropertyMetadata(1000, FrameworkPropertyMetadataOptions.AffectsRender));

    public int CursorMilliseconds
    {
        get => (int)GetValue(CursorMillisecondsProperty);
        set => SetValue(CursorMillisecondsProperty, value);
    }

    public static readonly DependencyProperty CursorMillisecondsProperty = DependencyProperty.Register(
        nameof(CursorMilliseconds), typeof(int), typeof(FunscriptTimeline),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public event EventHandler? PointsChanged;
    public event EventHandler<int>? CursorChanged;

    public IReadOnlyList<FunscriptPoint> GetPoints() => _points.ToArray();

    public void SetPoints(IEnumerable<FunscriptPoint> points)
    {
        _points.Clear();
        _points.AddRange(points.OrderBy(point => point.At));
        _undoHistory.Clear();
        _redoHistory.Clear();
        InvalidateVisual();
    }

    public void ClearPoints()
    {
        if (IsReadOnly) return;
        if (_points.Count == 0) return;
        RecordUndoSnapshot();
        _points.Clear();
        NotifyPointsChanged();
    }

    public bool AddOrReplaceAtCursor(int position)
    {
        if (IsReadOnly) return false;
        var point = new FunscriptPoint(SnapTime(CursorMilliseconds), Math.Clamp(position, 0, 100));
        var existing = _points.FirstOrDefault(item => item.At == point.At);
        if (_points.Any(item => item.At == point.At) && existing.Equals(point)) return false;

        RecordUndoSnapshot();
        _points.RemoveAll(item => item.At == point.At);
        _points.Add(point);
        SortAndNotify();
        return true;
    }

    public bool DeleteNearestAtCursor()
    {
        if (IsReadOnly) return false;
        var nearest = FindNearestPoint(CursorMilliseconds);
        if (nearest is null) return false;
        RecordUndoSnapshot();
        _points.Remove(nearest.Value);
        NotifyPointsChanged();
        return true;
    }

    public FunscriptPoint? NudgeNearest(int timeDelta, int positionDelta)
    {
        if (IsReadOnly) return null;
        var nearest = FindNearestPoint(CursorMilliseconds);
        if (nearest is null) return null;
        var moved = new FunscriptPoint(
            SnapTime(nearest.Value.At + timeDelta),
            Math.Clamp(nearest.Value.Pos + positionDelta, 0, 100));
        if (moved.Equals(nearest.Value)) return moved;

        RecordUndoSnapshot();
        _points.Remove(nearest.Value);
        _points.RemoveAll(point => point.At == moved.At);
        _points.Add(moved);
        SortAndNotify();
        return moved;
    }

    public FunscriptPoint? MoveNearestToCursor()
    {
        if (IsReadOnly) return null;
        var nearest = FindNearestPoint(CursorMilliseconds);
        if (nearest is null) return null;
        var moved = nearest.Value with { At = SnapTime(CursorMilliseconds) };
        if (moved.Equals(nearest.Value)) return moved;

        RecordUndoSnapshot();
        _points.Remove(nearest.Value);
        _points.RemoveAll(point => point.At == moved.At);
        _points.Add(moved);
        SortAndNotify();
        return moved;
    }

    public bool InvertNearest()
    {
        if (IsReadOnly) return false;
        var nearest = FindNearestPoint(CursorMilliseconds);
        if (nearest is null) return false;
        var inverted = nearest.Value with { Pos = 100 - nearest.Value.Pos };
        if (inverted.Equals(nearest.Value)) return false;

        RecordUndoSnapshot();
        _points.Remove(nearest.Value);
        _points.Add(inverted);
        SortAndNotify();
        return true;
    }

    public int? FindPreviousPointTime(int milliseconds) =>
        _points.Where(point => point.At < milliseconds).Select(point => (int?)point.At).LastOrDefault();

    public int? FindNextPointTime(int milliseconds) =>
        _points.Where(point => point.At > milliseconds).Select(point => (int?)point.At).FirstOrDefault();

    public bool Undo()
    {
        if (IsReadOnly) return false;
        if (_undoHistory.Count == 0) return false;
        _redoHistory.Push(_points.ToArray());
        ApplySnapshot(_undoHistory.Pop());
        return true;
    }

    public bool Redo()
    {
        if (IsReadOnly) return false;
        if (_redoHistory.Count == 0) return false;
        _undoHistory.Push(_points.ToArray());
        ApplySnapshot(_redoHistory.Pop());
        return true;
    }

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        context.DrawRectangle(BackgroundBrush, null, new Rect(RenderSize));
        if (ActualWidth <= 1 || ActualHeight <= 1) return;

        for (var value = 0; value <= 100; value += 25)
        {
            var y = PositionToY(value);
            context.DrawLine(GridPen, new Point(0, y), new Point(ActualWidth, y));
            DrawText(context, value.ToString(CultureInfo.InvariantCulture), new Point(4, Math.Max(0, y - 15)));
        }

        var seconds = Math.Max(1, (int)Math.Ceiling(DurationMilliseconds / 1000d));
        var interval = seconds <= 10 ? 1 : seconds <= 30 ? 5 : 10;
        for (var second = 0; second <= seconds; second += interval)
        {
            var x = TimeToX(second * 1000);
            context.DrawLine(GridPen, new Point(x, 0), new Point(x, ActualHeight));
            DrawText(context, $"{second}s", new Point(x + 3, ActualHeight - 18));
        }

        if (_points.Count > 0)
        {
            var geometry = new StreamGeometry();
            using (var drawing = geometry.Open())
            {
                drawing.BeginFigure(ToPoint(_points[0]), false, false);
                foreach (var point in _points.Skip(1)) drawing.LineTo(ToPoint(point), true, false);
            }
            geometry.Freeze();
            context.DrawGeometry(null, CurvePen, geometry);
            foreach (var point in _points) context.DrawEllipse(PointBrush, null, ToPoint(point), 3.5, 3.5);
        }

        var cursorX = TimeToX(CursorMilliseconds);
        context.DrawLine(CursorPen, new Point(cursorX, 0), new Point(cursorX, ActualHeight));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (IsReadOnly)
        {
            CursorChanged?.Invoke(this, SnapTime(XToTime(e.GetPosition(this).X)));
            e.Handled = true;
            return;
        }
        CaptureMouse();
        _draggingPoint = true;
        _dragStartPoints = _points.ToArray();
        _draggedPoint = FindPointNear(e.GetPosition(this));
        UpdateDraggedPoint(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_draggingPoint && e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateDraggedPoint(e.GetPosition(this));
            e.Handled = true;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_draggingPoint) return;
        UpdateDraggedPoint(e.GetPosition(this));
        FinishPointDrag();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_draggingPoint) FinishPointDrag();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (IsReadOnly) return;
        if (_points.Count == 0) return;
        var time = XToTime(e.GetPosition(this).X);
        var tolerance = Math.Max(20, (int)(DurationMilliseconds / Math.Max(1, ActualWidth) * 8));
        var nearest = FindNearestPoint(time);
        if (nearest is null || Math.Abs(nearest.Value.At - time) > tolerance) return;
        RecordUndoSnapshot();
        _points.Remove(nearest.Value);
        NotifyPointsChanged();
        e.Handled = true;
    }

    private void UpdateDraggedPoint(Point position)
    {
        var next = new FunscriptPoint(SnapTime(XToTime(position.X)), YToPosition(position.Y));
        if (_draggedPoint is { } current && current.Equals(next))
        {
            CursorChanged?.Invoke(this, next.At);
            return;
        }

        if (_draggedPoint is { } previous) _points.Remove(previous);
        _points.RemoveAll(point => point.At == next.At);
        _points.Add(next);
        _points.Sort((left, right) => left.At.CompareTo(right.At));
        _draggedPoint = next;
        NotifyPointsChanged();
        CursorChanged?.Invoke(this, next.At);
    }

    private void FinishPointDrag()
    {
        _draggingPoint = false;
        if (_dragStartPoints is { } start && !start.SequenceEqual(_points))
        {
            _undoHistory.Push(start);
            _redoHistory.Clear();
        }
        _dragStartPoints = null;
        _draggedPoint = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    private FunscriptPoint? FindPointNear(Point position)
    {
        const double hitRadius = 10;
        return _points
            .Select(point => (Point: point, Distance: (ToPoint(point) - position).LengthSquared))
            .Where(candidate => candidate.Distance <= hitRadius * hitRadius)
            .OrderBy(candidate => candidate.Distance)
            .Select(candidate => (FunscriptPoint?)candidate.Point)
            .FirstOrDefault();
    }

    private FunscriptPoint? FindNearestPoint(int milliseconds) =>
        _points.Count == 0 ? null : _points.MinBy(point => Math.Abs(point.At - milliseconds));

    private int SnapTime(int milliseconds) =>
        Math.Clamp((int)Math.Round(milliseconds / (double)TimeStepMilliseconds) * TimeStepMilliseconds, 0, DurationMilliseconds);

    private void RecordUndoSnapshot()
    {
        _undoHistory.Push(_points.ToArray());
        _redoHistory.Clear();
    }

    private void ApplySnapshot(IEnumerable<FunscriptPoint> snapshot)
    {
        _points.Clear();
        _points.AddRange(snapshot);
        NotifyPointsChanged();
    }

    private void SortAndNotify()
    {
        _points.Sort((left, right) => left.At.CompareTo(right.At));
        NotifyPointsChanged();
    }

    private void NotifyPointsChanged()
    {
        InvalidateVisual();
        PointsChanged?.Invoke(this, EventArgs.Empty);
    }

    private Point ToPoint(FunscriptPoint point) => new(TimeToX(point.At), PositionToY(point.Pos));
    private double TimeToX(int milliseconds) => Math.Clamp(milliseconds, 0, Math.Max(1, DurationMilliseconds)) * ActualWidth / Math.Max(1, DurationMilliseconds);
    private int XToTime(double x) => (int)Math.Round(Math.Clamp(x, 0, ActualWidth) / Math.Max(1, ActualWidth) * Math.Max(1, DurationMilliseconds));
    private double PositionToY(int position) => (100 - Math.Clamp(position, 0, 100)) / 100d * ActualHeight;
    private int YToPosition(double y) => (int)Math.Round((1 - Math.Clamp(y, 0, ActualHeight) / Math.Max(1, ActualHeight)) * 100);

    private static void DrawText(DrawingContext context, string text, Point point)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 10, LabelBrush, 1);
        context.DrawText(formatted, point);
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
