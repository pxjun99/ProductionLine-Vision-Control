using System.Drawing.Drawing2D;

namespace ProductionLine.TrainingTool;

internal sealed class AnnotationCanvas : Control
{
    private readonly Color[] _colors =
    [
        Color.FromArgb(0, 210, 120), Color.FromArgb(240, 80, 80), Color.FromArgb(255, 180, 30),
        Color.FromArgb(80, 160, 255), Color.FromArgb(200, 100, 255), Color.FromArgb(0, 210, 220)
    ];

    private Image? _image;
    private List<YoloAnnotation> _annotations = [];
    private Point _dragStart;
    private Point _dragCurrent;
    private bool _drawing;
    private int _mouseDownHitIndex = -1;

    public IReadOnlyList<string> Classes { get; set; } = [];
    public int ActiveClassId { get; set; }
    public int SelectedIndex { get; private set; } = -1;
    public bool HasImage => _image != null;
    public Size ImageSize => _image?.Size ?? Size.Empty;
    public IReadOnlyList<YoloAnnotation> Annotations => _annotations;

    public event EventHandler? AnnotationsChanged;
    public event EventHandler? SelectionChanged;

    public AnnotationCanvas()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(28, 33, 42);
        Cursor = Cursors.Cross;
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    public void SetImage(Image? image, IEnumerable<YoloAnnotation>? annotations = null)
    {
        _image?.Dispose();
        _image = image;
        _annotations = annotations?.ToList() ?? [];
        SelectedIndex = -1;
        _drawing = false;
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SelectAnnotation(int index)
    {
        SelectedIndex = index >= 0 && index < _annotations.Count ? index : -1;
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void DeleteSelected()
    {
        if (SelectedIndex < 0 || SelectedIndex >= _annotations.Count) return;
        _annotations.RemoveAt(SelectedIndex);
        SelectedIndex = -1;
        Invalidate();
        AnnotationsChanged?.Invoke(this, EventArgs.Empty);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (_image == null)
        {
            using var brush = new SolidBrush(Color.FromArgb(180, 190, 205));
            using var font = new Font(Font.FontFamily, 14, FontStyle.Bold);
            const string message = "프로젝트를 열고 사진을 가져오세요.";
            var size = e.Graphics.MeasureString(message, font);
            e.Graphics.DrawString(message, font, brush, (Width - size.Width) / 2, (Height - size.Height) / 2);
            return;
        }

        var display = DisplayRectangleForImage();
        e.Graphics.DrawImage(_image, display);
        for (var index = 0; index < _annotations.Count; index++)
            DrawAnnotation(e.Graphics, _annotations[index], index, display);

        if (_drawing)
        {
            var preview = Normalize(_dragStart, _dragCurrent);
            using var pen = new Pen(Color.White, 2) { DashStyle = DashStyle.Dash };
            e.Graphics.DrawRectangle(pen, preview);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (_image == null || e.Button != MouseButtons.Left) return;
        var display = DisplayRectangleForImage();
        if (!display.Contains(e.Location)) return;

        _mouseDownHitIndex = -1;
        for (var index = _annotations.Count - 1; index >= 0; index--)
        {
            if (ImageToScreen(_annotations[index].Box, display).Contains(e.Location))
            {
                _mouseDownHitIndex = index;
                break;
            }
        }

        // 기존 박스 안에서도 드래그를 시작할 수 있어야 crack 같은 내부 박스를 추가할 수 있습니다.
        // 움직이지 않고 클릭만 하면 MouseUp에서 기존 박스를 선택합니다.
        _drawing = true;
        _dragStart = _dragCurrent = e.Location;
        Capture = true;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_drawing) return;
        _dragCurrent = ClampToDisplay(e.Location, DisplayRectangleForImage());
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_drawing || _image == null) return;
        _drawing = false;
        Capture = false;
        _dragCurrent = ClampToDisplay(e.Location, DisplayRectangleForImage());
        var screenBox = Normalize(_dragStart, _dragCurrent);
        if (screenBox.Width >= 6 && screenBox.Height >= 6)
        {
            var imageBox = ScreenToImage(screenBox, DisplayRectangleForImage());
            _annotations.Add(new YoloAnnotation(ActiveClassId, imageBox));
            SelectedIndex = _annotations.Count - 1;
            AnnotationsChanged?.Invoke(this, EventArgs.Empty);
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            SelectedIndex = _mouseDownHitIndex;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
        _mouseDownHitIndex = -1;
        Invalidate();
    }

    private void DrawAnnotation(Graphics graphics, YoloAnnotation annotation, int index, RectangleF display)
    {
        var box = ImageToScreen(annotation.Box, display);
        var color = _colors[Math.Abs(annotation.ClassId) % _colors.Length];
        using var pen = new Pen(index == SelectedIndex ? Color.White : color, index == SelectedIndex ? 4 : 3);
        graphics.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height);

        var className = annotation.ClassId >= 0 && annotation.ClassId < Classes.Count
            ? Classes[annotation.ClassId]
            : $"class_{annotation.ClassId}";
        using var font = new Font("Segoe UI", 9, FontStyle.Bold);
        var textSize = graphics.MeasureString(className, font);
        var label = new RectangleF(box.X, Math.Max(display.Top, box.Y - textSize.Height - 4), textSize.Width + 8, textSize.Height + 4);
        using var brush = new SolidBrush(color);
        graphics.FillRectangle(brush, label);
        graphics.DrawString(className, font, Brushes.White, label.X + 4, label.Y + 2);
    }

    private RectangleF DisplayRectangleForImage()
    {
        if (_image == null || Width <= 0 || Height <= 0) return RectangleF.Empty;
        var scale = Math.Min(Width / (float)_image.Width, Height / (float)_image.Height);
        var width = _image.Width * scale;
        var height = _image.Height * scale;
        return new RectangleF((Width - width) / 2, (Height - height) / 2, width, height);
    }

    private RectangleF ImageToScreen(RectangleF box, RectangleF display) => new(
        display.Left + box.Left / _image!.Width * display.Width,
        display.Top + box.Top / _image.Height * display.Height,
        box.Width / _image.Width * display.Width,
        box.Height / _image.Height * display.Height);

    private RectangleF ScreenToImage(RectangleF box, RectangleF display) => new(
        (box.Left - display.Left) / display.Width * _image!.Width,
        (box.Top - display.Top) / display.Height * _image.Height,
        box.Width / display.Width * _image.Width,
        box.Height / display.Height * _image.Height);

    private static Rectangle Normalize(Point start, Point end) => new(
        Math.Min(start.X, end.X), Math.Min(start.Y, end.Y),
        Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y));

    private static Point ClampToDisplay(Point point, RectangleF display) => new(
        Math.Clamp(point.X, (int)Math.Ceiling(display.Left), (int)Math.Floor(display.Right)),
        Math.Clamp(point.Y, (int)Math.Ceiling(display.Top), (int)Math.Floor(display.Bottom)));

    protected override void Dispose(bool disposing)
    {
        if (disposing) _image?.Dispose();
        base.Dispose(disposing);
    }
}
