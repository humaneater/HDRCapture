using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using HdrCapture.Capture;
using HdrCapture.Infrastructure;
using Path = System.Windows.Shapes.Path;

namespace HdrCapture.Ui;

/// <summary>
/// Frozen full desktop overlay: every display shows its captured frame, the user drags a
/// rectangle in physical pixels and confirms by releasing the left button.
/// Esc or the right mouse button cancels.
/// </summary>
internal sealed class SelectionOverlay : IDisposable
{
    private const int MinimumSide = 4;
    private const int VkEscape = 0x1B;
    private const int VkLeftButton = 0x01;

    private readonly List<OverlayWindow> _windows = [];
    private readonly TaskCompletionSource<PixelRect?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DispatcherTimer _timer;
    private int _startX;
    private int _startY;
    private int _currentX;
    private int _currentY;
    private bool _dragging;
    private bool _finished;

    public SelectionOverlay(IReadOnlyList<CapturedMonitor> captures)
    {
        foreach (var capture in captures)
        {
            _windows.Add(new OverlayWindow(this, capture));
        }

        _timer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _timer.Tick += OnTick;
    }

    public Task<PixelRect?> RunAsync()
    {
        foreach (var window in _windows)
        {
            window.Show();
        }

        var cursor = GetCursorPosition();
        var active = _windows.FirstOrDefault(window => window.ContainsScreenPoint(cursor)) ?? _windows[0];
        active.Activate();
        active.Focus();
        _timer.Start();
        Log.Info("[timing] selection overlay shown");
        return _completion.Task;
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
        foreach (var window in _windows)
        {
            window.Dispose();
        }

        _windows.Clear();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_finished)
        {
            return;
        }

        if ((GetAsyncKeyState(VkEscape) & 0x8000) != 0)
        {
            Cancel();
            return;
        }

        if (_dragging && (GetAsyncKeyState(VkLeftButton) & 0x8000) == 0)
        {
            var cursor = GetCursorPosition();
            Complete((int)Math.Round(cursor.X), (int)Math.Round(cursor.Y));
        }
    }

    private void BeginDrag(int x, int y)
    {
        if (_finished)
        {
            return;
        }

        _dragging = true;
        _startX = x;
        _startY = y;
        _currentX = x;
        _currentY = y;
        Update();
    }

    private void UpdateDrag(int x, int y)
    {
        if (_finished || !_dragging)
        {
            return;
        }

        _currentX = x;
        _currentY = y;
        Update();
    }

    private void Complete(int x, int y)
    {
        if (_finished)
        {
            return;
        }

        _currentX = x;
        _currentY = y;
        var rect = PixelRect.FromCorners(_startX, _startY, _currentX, _currentY);
        Finish(rect.Width >= MinimumSide && rect.Height >= MinimumSide ? rect : null, "confirmed");
    }

    private void Cancel()
    {
        if (_finished)
        {
            return;
        }

        Finish(null, "cancelled");
    }

    private void Finish(PixelRect? result, string reason)
    {
        _finished = true;
        _dragging = false;
        _timer.Stop();

        foreach (var window in _windows)
        {
            try
            {
                window.ReleaseMouseCapture();
                window.Hide();
            }
            catch (Exception ex)
            {
                Log.Error("Failed to hide the selection overlay.", ex);
            }
        }

        Log.Info($"Selection overlay {reason}.");
        _completion.TrySetResult(result);
    }

    private void Update()
    {
        var rect = PixelRect.FromCorners(_startX, _startY, _currentX, _currentY);
        foreach (var window in _windows)
        {
            window.UpdateSelection(rect, _dragging);
        }
    }

    internal static Point GetCursorPosition()
    {
        return GetCursorPos(out var point) ? new Point(point.X, point.Y) : new Point(0, 0);
    }

    private sealed class OverlayWindow : Window
    {
        private static readonly Brush DimBrush = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0));
        private static readonly Brush SelectionBorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0xFF));
        private static readonly Brush LabelBrush = new SolidColorBrush(Color.FromArgb(0xE0, 0, 0, 0));

        private readonly SelectionOverlay _owner;
        private readonly CapturedMonitor _capture;
        private readonly Image _image;
        private readonly double _scale;
        private readonly Path _dim;
        private readonly Rectangle _border;
        private readonly Border _label;
        private readonly TextBlock _labelText;
        private readonly Canvas _canvas;
        private readonly double _width;
        private readonly double _height;

        public OverlayWindow(SelectionOverlay owner, CapturedMonitor capture)
        {
            _owner = owner;
            _capture = capture;
            _scale = capture.Monitor.DpiScale <= 0 ? 1.0 : capture.Monitor.DpiScale;
            _width = capture.Monitor.Width / _scale;
            _height = capture.Monitor.Height / _scale;

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            AllowsTransparency = false;
            ShowActivated = true;
            Background = Brushes.Black;
            Cursor = Cursors.Cross;
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = capture.Monitor.X / _scale;
            Top = capture.Monitor.Y / _scale;
            Width = _width;
            Height = _height;

            _image = new Image
            {
                Source = capture.Preview,
                Stretch = Stretch.Fill,
                SnapsToDevicePixels = true
            };

            _dim = new Path
            {
                Fill = DimBrush,
                IsHitTestVisible = false,
                Data = new RectangleGeometry(new Rect(0, 0, _width, _height))
            };

            _border = new Rectangle
            {
                Stroke = SelectionBorderBrush,
                StrokeThickness = 1,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };

            _labelText = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 13,
                FontFamily = new FontFamily("Segoe UI"),
                Padding = new Thickness(6, 2, 6, 2)
            };
            _label = new Border
            {
                Background = LabelBrush,
                Child = _labelText,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };

            _canvas = new Canvas { IsHitTestVisible = false };
            _canvas.Children.Add(_dim);
            _canvas.Children.Add(_border);
            _canvas.Children.Add(_label);

            var root = new Grid();
            root.Children.Add(_image);
            root.Children.Add(_canvas);
            Content = root;

            MouseLeftButtonDown += OnMouseLeftButtonDown;
            MouseMove += OnMouseMove;
            MouseLeftButtonUp += OnMouseLeftButtonUp;
            MouseRightButtonDown += OnMouseRightButtonDown;
            PreviewKeyDown += OnPreviewKeyDown;
            PreviewMouseRightButtonDown += OnMouseRightButtonDown;
        }

        public void Dispose()
        {
            MouseLeftButtonDown -= OnMouseLeftButtonDown;
            MouseMove -= OnMouseMove;
            MouseLeftButtonUp -= OnMouseLeftButtonUp;
            MouseRightButtonDown -= OnMouseRightButtonDown;
            PreviewKeyDown -= OnPreviewKeyDown;
            PreviewMouseRightButtonDown -= OnMouseRightButtonDown;
            _image.Source = null;
            Content = null;
            Close();
        }

        public bool ContainsScreenPoint(Point screen)
        {
            var monitor = _capture.Monitor;
            return screen.X >= monitor.X && screen.X < monitor.Right &&
                   screen.Y >= monitor.Y && screen.Y < monitor.Bottom;
        }

        public void UpdateSelection(PixelRect screenRect, bool active)
        {
            if (!active || screenRect.IsEmpty)
            {
                _dim.Data = new RectangleGeometry(new Rect(0, 0, _width, _height));
                _border.Visibility = Visibility.Collapsed;
                _label.Visibility = Visibility.Collapsed;
                return;
            }

            var monitor = _capture.Monitor;
            var left = (screenRect.X - monitor.X) / _scale;
            var top = (screenRect.Y - monitor.Y) / _scale;
            var right = (screenRect.Right - monitor.X) / _scale;
            var bottom = (screenRect.Bottom - monitor.Y) / _scale;

            var visible = Rect.Intersect(
                new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top)),
                new Rect(0, 0, _width, _height));

            var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
            group.Children.Add(new RectangleGeometry(new Rect(0, 0, _width, _height)));
            if (!visible.IsEmpty)
            {
                group.Children.Add(new RectangleGeometry(visible));
            }

            _dim.Data = group;

            if (visible.IsEmpty)
            {
                _border.Visibility = Visibility.Collapsed;
                _label.Visibility = Visibility.Collapsed;
                return;
            }

            _border.Visibility = Visibility.Visible;
            _border.Width = visible.Width;
            _border.Height = visible.Height;
            Canvas.SetLeft(_border, visible.Left);
            Canvas.SetTop(_border, visible.Top);

            _labelText.Text = $"{screenRect.Width} x {screenRect.Height}";
            var isAnchorDisplay = monitor.Contains(screenRect.Right - 1, screenRect.Bottom - 1);
            if (isAnchorDisplay)
            {
                _label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var size = _label.DesiredSize;
                var labelLeft = Math.Min(visible.Right - size.Width, _width - size.Width);
                var labelTop = visible.Bottom + 6;
                if (labelTop + size.Height > _height)
                {
                    labelTop = visible.Bottom - size.Height - 6;
                }

                _label.Visibility = Visibility.Visible;
                Canvas.SetLeft(_label, Math.Max(0, labelLeft));
                Canvas.SetTop(_label, Math.Clamp(labelTop, 0, Math.Max(0, _height - size.Height)));
            }
            else
            {
                _label.Visibility = Visibility.Collapsed;
            }
        }

        private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left)
            {
                return;
            }

            var point = ToScreen(e.GetPosition(this));
            CaptureMouse();
            _owner.BeginDrag((int)Math.Round(point.X), (int)Math.Round(point.Y));
            e.Handled = true;
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            var point = ToScreen(e.GetPosition(this));
            _owner.UpdateDrag((int)Math.Round(point.X), (int)Math.Round(point.Y));
        }

        private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var point = ToScreen(e.GetPosition(this));
            _owner.Complete((int)Math.Round(point.X), (int)Math.Round(point.Y));
            e.Handled = true;
        }

        private void OnMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            _owner.Cancel();
            e.Handled = true;
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                _owner.Cancel();
                e.Handled = true;
            }
        }

        private Point ToScreen(Point local) =>
            new(
                _capture.Monitor.X + (local.X * _scale),
                _capture.Monitor.Y + (local.Y * _scale));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
