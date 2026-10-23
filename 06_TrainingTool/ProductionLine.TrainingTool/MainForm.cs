using System.Diagnostics;
using System.Text;

namespace ProductionLine.TrainingTool;

public sealed class MainForm : Form
{
    private readonly AnnotationCanvas _canvas = new() { Dock = DockStyle.Fill };
    private readonly ListBox _images = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _classes = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ListBox _boxes = new() { Dock = DockStyle.Fill };
    private readonly TextBox _newClass = new() { Dock = DockStyle.Fill, PlaceholderText = "새 클래스 이름" };
    private readonly Label _projectLabel = new() { Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = Color.FromArgb(70, 82, 98) };
    private readonly Label _imageStatus = new() { Dock = DockStyle.Fill, ForeColor = Color.FromArgb(70, 82, 98) };
    private readonly RichTextBox _log = new() { Dock = DockStyle.Fill, ReadOnly = true, BackColor = Color.FromArgb(20, 24, 31), ForeColor = Color.FromArgb(215, 225, 235), Font = new Font("Consolas", 9) };
    private readonly TextBox _python = new() { Dock = DockStyle.Fill };
    private readonly TextBox _model = new() { Dock = DockStyle.Fill, Text = "yolo26n.pt" };
    private readonly TextBox _device = new() { Dock = DockStyle.Fill, PlaceholderText = "자동: 공백 / CPU: cpu / NVIDIA: 0" };
    private readonly NumericUpDown _epochs = new() { Dock = DockStyle.Fill, Minimum = 1, Maximum = 1000, Value = 100 };
    private readonly NumericUpDown _imageSize = new() { Dock = DockStyle.Fill, Minimum = 320, Maximum = 2048, Increment = 32, Value = 640 };
    private readonly Button _trainButton;
    private readonly Button _cancelButton;

    private YoloProject? _project;
    private List<string> _imagePaths = [];
    private string? _currentImage;
    private bool _dirty;
    private bool _changingSelection;
    private Process? _trainingProcess;

    public MainForm()
    {
        Text = "아이폰 검사 YOLO 라벨링·학습 도구";
        WindowState = FormWindowState.Maximized;
        MinimumSize = new Size(1280, 780);
        Font = new Font("맑은 고딕", 9.5f);
        BackColor = Color.FromArgb(242, 245, 249);
        KeyPreview = true;

        _python.Text = FindPython();
        _trainButton = Button("YOLO 학습 + ONNX 변환", async () => await TrainAsync(), Color.FromArgb(31, 111, 203), Color.White);
        _cancelButton = Button("학습 중지", CancelTraining, Color.FromArgb(210, 70, 75), Color.White);
        _cancelButton.Enabled = false;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Padding = new Padding(12) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildWorkspace(), 0, 1);
        Controls.Add(root);

        _images.SelectedIndexChanged += (_, _) => SelectImage();
        _classes.SelectedIndexChanged += (_, _) => _canvas.ActiveClassId = Math.Max(0, _classes.SelectedIndex);
        _boxes.SelectedIndexChanged += (_, _) => { if (!_changingSelection) _canvas.SelectAnnotation(_boxes.SelectedIndex); };
        _canvas.AnnotationsChanged += (_, _) => { _dirty = true; RefreshBoxList(); RefreshImageStatus(); };
        _canvas.SelectionChanged += (_, _) => SelectBoxInList();
        FormClosing += (_, e) => { if (!SaveCurrentIfNeeded()) e.Cancel = true; };
        KeyDown += MainFormKeyDown;
    }

    private Control BuildHeader()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, BackColor = Color.White, Padding = new Padding(10, 8, 10, 8) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        panel.Controls.Add(Button("새 프로젝트", CreateProject), 0, 0);
        panel.Controls.Add(Button("프로젝트 열기", OpenProject), 1, 0);
        panel.Controls.Add(Button("사진 가져오기", ImportImages), 2, 0);
        panel.Controls.Add(Button("라벨 저장", SaveCurrent), 3, 0);
        panel.Controls.Add(_projectLabel, 4, 0);
        panel.Controls.Add(Button("사용법", ShowHelp), 5, 0);
        return panel;
    }

    private Control BuildWorkspace()
    {
        var workspace = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1
        };
        workspace.RowStyles.Add(new RowStyle(SizeType.Percent, 73));
        workspace.RowStyles.Add(new RowStyle(SizeType.Percent, 27));

        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 1,
            ColumnCount = 3
        };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 365));

        var imageCard = Card("사진 목록", BuildImageList());
        var canvasCard = Card("라벨링 화면 · 마우스로 제품 또는 깨짐 영역을 드래그", _canvas);
        var toolsCard = Card("라벨과 학습", BuildTools());
        imageCard.Margin = new Padding(0, 0, 8, 8);
        canvasCard.Margin = new Padding(0, 0, 8, 8);
        toolsCard.Margin = new Padding(0, 0, 0, 8);

        top.Controls.Add(imageCard, 0, 0);
        top.Controls.Add(canvasCard, 1, 0);
        top.Controls.Add(toolsCard, 2, 0);

        var logCard = Card("학습 로그", _log);
        workspace.Controls.Add(top, 0, 0);
        workspace.Controls.Add(logCard, 0, 1);
        return workspace;
    }

    private Control BuildImageList()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(8) };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.Controls.Add(_images, 0, 0);
        layout.Controls.Add(_imageStatus, 0, 1);
        var navigation = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        navigation.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        navigation.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        navigation.Controls.Add(Button("◀ 이전", PreviousImage), 0, 0);
        navigation.Controls.Add(Button("다음 ▶", NextImage), 1, 0);
        layout.Controls.Add(navigation, 0, 2);
        return layout;
    }

    private Control BuildTools()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(10),
            AutoScroll = true,
            GrowStyle = TableLayoutPanelGrowStyle.AddRows
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.Controls.Add(Label("현재 라벨 클래스"));
        layout.Controls.Add(_classes);
        var addClass = new TableLayoutPanel { Dock = DockStyle.Top, Height = 38, ColumnCount = 2 };
        addClass.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        addClass.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        addClass.Controls.Add(_newClass, 0, 0);
        addClass.Controls.Add(Button("추가", AddClass), 1, 0);
        layout.Controls.Add(addClass);
        layout.Controls.Add(Label("현재 사진의 박스"));
        _boxes.Height = 115;
        layout.Controls.Add(_boxes);
        layout.Controls.Add(Button("선택 박스 삭제", () => _canvas.DeleteSelected(), Color.FromArgb(230, 235, 241), Color.FromArgb(40, 50, 65)));
        layout.Controls.Add(Label("Python 실행 파일"));
        layout.Controls.Add(_python);
        layout.Controls.Add(Label("기본 YOLO 모델"));
        layout.Controls.Add(_model);
        layout.Controls.Add(TwoFields("학습 횟수", _epochs, "이미지 크기", _imageSize));
        layout.Controls.Add(Label("학습 장치"));
        layout.Controls.Add(_device);
        layout.Controls.Add(Button("Python·YOLO 확인", async () => await CheckEnvironmentAsync()));
        layout.Controls.Add(Button("YOLO 환경 설치", async () => await InstallEnvironmentAsync(), Color.FromArgb(230, 235, 241), Color.FromArgb(40, 50, 65)));
        layout.Controls.Add(Button("학습 데이터 만들기", PrepareDataset));
        layout.Controls.Add(_trainButton);
        layout.Controls.Add(_cancelButton);
        return layout;
    }

    private void CreateProject()
    {
        if (!SaveCurrentIfNeeded()) return;
        using var dialog = new FolderBrowserDialog { Description = "새 학습 프로젝트를 저장할 빈 폴더를 선택하세요.", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { LoadProject(YoloProject.Create(dialog.SelectedPath)); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void OpenProject()
    {
        if (!SaveCurrentIfNeeded()) return;
        using var dialog = new FolderBrowserDialog { Description = "images 폴더와 classes.txt가 있는 학습 프로젝트를 선택하세요.", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { LoadProject(YoloProject.Open(dialog.SelectedPath)); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void LoadProject(YoloProject project)
    {
        _project = project;
        _projectLabel.Text = project.Root;
        _classes.DataSource = null;
        _classes.DataSource = project.Classes.ToList();
        _classes.SelectedIndex = 0;
        _canvas.Classes = project.Classes;
        RefreshImages();
        AppendLog($"프로젝트 열기: {project.Root}");
    }

    private void ImportImages()
    {
        if (_project == null) { MessageBox.Show(this, "먼저 새 프로젝트를 만들거나 기존 프로젝트를 여세요."); return; }
        using var dialog = new OpenFileDialog
        {
            Title = "아이폰 학습 사진 선택",
            Filter = "이미지|*.jpg;*.jpeg;*.png;*.bmp",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            SaveCurrentIfNeeded();
            var count = _project.ImportImages(dialog.FileNames);
            RefreshImages();
            AppendLog($"사진 {count}장 가져오기 완료");
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void RefreshImages()
    {
        _imagePaths = _project?.GetImages().ToList() ?? [];
        _changingSelection = true;
        _images.DataSource = _imagePaths.Select(Path.GetFileName).ToList();
        _changingSelection = false;
        if (_imagePaths.Count > 0) _images.SelectedIndex = 0;
        else { _currentImage = null; _canvas.SetImage(null); RefreshImageStatus(); }
    }

    private void SelectImage()
    {
        if (_changingSelection || _project == null || _images.SelectedIndex < 0 || _images.SelectedIndex >= _imagePaths.Count) return;
        if (!SaveCurrentIfNeeded())
        {
            _changingSelection = true;
            _images.SelectedIndex = _currentImage == null ? -1 : _imagePaths.IndexOf(_currentImage);
            _changingSelection = false;
            return;
        }

        try
        {
            _currentImage = _imagePaths[_images.SelectedIndex];
            var bytes = File.ReadAllBytes(_currentImage);
            using var stream = new MemoryStream(bytes);
            using var source = Image.FromStream(stream);
            var image = new Bitmap(source);
            var annotations = _project.LoadAnnotations(_currentImage, image.Size);
            _canvas.SetImage(image, annotations);
            _dirty = false;
            RefreshBoxList();
            RefreshImageStatus();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void SaveCurrent()
    {
        if (_project == null || _currentImage == null || !_canvas.HasImage) return;
        try
        {
            _project.SaveAnnotations(_currentImage, _canvas.ImageSize, _canvas.Annotations);
            _dirty = false;
            RefreshImageStatus();
            AppendLog($"라벨 저장: {Path.GetFileName(_currentImage)} · 박스 {_canvas.Annotations.Count}개");
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private bool SaveCurrentIfNeeded()
    {
        if (!_dirty) return true;
        SaveCurrent();
        return !_dirty;
    }

    private void AddClass()
    {
        if (_project == null) return;
        try
        {
            _project.AddClass(_newClass.Text);
            _newClass.Clear();
            _classes.DataSource = null;
            _classes.DataSource = _project.Classes.ToList();
            _classes.SelectedIndex = _project.Classes.Count - 1;
            _canvas.Classes = _project.Classes;
            _canvas.Invalidate();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void RefreshBoxList()
    {
        var items = _canvas.Annotations.Select((annotation, index) =>
        {
            var className = _project != null && annotation.ClassId < _project.Classes.Count ? _project.Classes[annotation.ClassId] : $"class_{annotation.ClassId}";
            return $"{index + 1}. {className}  ({annotation.Box.Width:F0}×{annotation.Box.Height:F0})";
        }).ToList();
        _changingSelection = true;
        _boxes.DataSource = items;
        _boxes.SelectedIndex = _canvas.SelectedIndex;
        _changingSelection = false;
    }

    private void SelectBoxInList()
    {
        _changingSelection = true;
        _boxes.SelectedIndex = _canvas.SelectedIndex;
        _changingSelection = false;
    }

    private void RefreshImageStatus()
    {
        if (_project == null) { _imageStatus.Text = "프로젝트 없음"; return; }
        var saved = _currentImage != null && File.Exists(_project.LabelPath(_currentImage));
        _imageStatus.Text = $"총 {_imagePaths.Count}장 · 현재 박스 {_canvas.Annotations.Count}개 · {(saved && !_dirty ? "저장됨" : "저장 필요")}";
    }

    private void PreviousImage() { if (_images.SelectedIndex > 0) _images.SelectedIndex--; }
    private void NextImage() { if (_images.SelectedIndex >= 0 && _images.SelectedIndex < _images.Items.Count - 1) _images.SelectedIndex++; }

    private void PrepareDataset()
    {
        if (_project == null) return;
        try
        {
            SaveCurrentIfNeeded();
            var summary = _project.PrepareDataset();
            AppendLog($"데이터셋 생성 완료 · 학습 {summary.TrainCount}장 · 검증 {summary.ValidationCount}장 · 미저장 제외 {summary.UnlabeledCount}장");
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task CheckEnvironmentAsync()
    {
        var code = "import sys, torch, ultralytics; print('Python', sys.version); print('PyTorch', torch.__version__); print('Ultralytics', ultralytics.__version__); print('CUDA', torch.cuda.is_available())";
        await RunPythonAsync(["-c", code], "Python·YOLO 환경 확인", false);
    }

    private async Task InstallEnvironmentAsync()
    {
        var answer = MessageBox.Show(this, "선택한 Python에 Ultralytics와 ONNX를 설치합니다. 인터넷 연결이 필요하고 시간이 걸릴 수 있습니다. 계속할까요?", "YOLO 환경 설치", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;
        await RunPythonAsync(["-m", "pip", "install", "--upgrade", "ultralytics", "onnx"], "YOLO 환경 설치", false);
    }

    private async Task TrainAsync()
    {
        if (_project == null) { MessageBox.Show(this, "학습 프로젝트를 먼저 여세요."); return; }
        if (_trainingProcess != null) return;
        try
        {
            SaveCurrentIfNeeded();
            var summary = _project.PrepareDataset();
            AppendLog($"학습 시작 준비 · train={summary.TrainCount}, val={summary.ValidationCount}");
            var script = Path.Combine(AppContext.BaseDirectory, "train_yolo.py");
            if (!File.Exists(script)) throw new FileNotFoundException("학습 스크립트를 찾지 못했습니다.", script);
            var arguments = new List<string>
            {
                script,
                "--data", _project.DataYamlPath,
                "--model", _model.Text.Trim(),
                "--epochs", ((int)_epochs.Value).ToString(),
                "--imgsz", ((int)_imageSize.Value).ToString(),
                "--output", _project.ModelsDirectory
            };
            if (!string.IsNullOrWhiteSpace(_device.Text)) { arguments.Add("--device"); arguments.Add(_device.Text.Trim()); }
            await RunPythonAsync(arguments, "YOLO 학습", true);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task RunPythonAsync(IEnumerable<string> arguments, string title, bool isTraining)
    {
        var python = ResolvePythonExecutable(_python.Text);
        if (python.Length == 0)
        {
            MessageBox.Show(this, "실행 가능한 Python을 찾지 못했습니다. Python 실행 파일 경로를 지정하세요.");
            return;
        }
        _python.Text = python;
        var start = new ProcessStartInfo
        {
            FileName = python,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        AppendLog($"[{title}] 시작");
        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) AppendLog(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) AppendLog(e.Data); };
        try
        {
            if (isTraining)
            {
                _trainingProcess = process;
                _trainButton.Enabled = false;
                _cancelButton.Enabled = true;
            }
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            AppendLog($"[{title}] 종료 코드 {process.ExitCode}");
            if (process.ExitCode != 0) MessageBox.Show(this, "작업에 실패했습니다. 아래 학습 로그를 확인하세요.", title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            else if (isTraining) MessageBox.Show(this, $"학습과 ONNX 변환이 완료되었습니다.\r\n{_project!.ModelsDirectory}", title);
        }
        catch (Exception ex) { AppendLog($"[{title}] 실행 실패: {ex.Message}"); ShowError(ex); }
        finally
        {
            if (isTraining)
            {
                _trainingProcess = null;
                _trainButton.Enabled = true;
                _cancelButton.Enabled = false;
            }
        }
    }

    private static string ResolvePythonExecutable(string configuredValue)
    {
        var configured = configuredValue.Trim().Trim('"');
        if (Path.IsPathFullyQualified(configured) && File.Exists(configured)) return configured;

        // WindowsApps의 python.exe는 Microsoft Store 실행 별칭이라 실제 실행이 실패할 수 있다.
        // 사용자가 단순히 "python"을 입력했거나 값이 비어 있으면 설치된 실제 실행 파일을 다시 찾는다.
        if (configured.Length == 0 || configured.Equals("python", StringComparison.OrdinalIgnoreCase) ||
            configured.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
        {
            var detected = FindPython();
            if (Path.IsPathFullyQualified(detected) && File.Exists(detected)) return detected;
        }

        return configured;
    }

    private void CancelTraining()
    {
        try
        {
            if (_trainingProcess is { HasExited: false })
            {
                _trainingProcess.Kill(true);
                AppendLog("학습 중지 요청");
            }
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void MainFormKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.S) { SaveCurrent(); e.SuppressKeyPress = true; }
        else if (e.KeyCode == Keys.Delete && !_newClass.Focused) { _canvas.DeleteSelected(); e.SuppressKeyPress = true; }
        else if (e.KeyCode == Keys.A && !_newClass.Focused) PreviousImage();
        else if (e.KeyCode == Keys.D && !_newClass.Focused) NextImage();
    }

    private void ShowHelp() => MessageBox.Show(this,
        "1. 새 프로젝트 생성\r\n2. 사진 가져오기\r\n3. 오른쪽에서 클래스 선택\r\n4. 사진에서 대상을 드래그해 박스 생성\r\n5. 라벨 저장\r\n6. 모든 사진을 라벨링한 뒤 학습 실행\r\n\r\n" +
        "권장 라벨: target_iphone=목표 기종 전체, other_phone=다른 휴대폰 전체, crack=깨진 영역\r\n" +
        "빈 배경 사진은 박스를 만들지 않고 라벨 저장을 눌러 음성 샘플로 포함할 수 있습니다.",
        "라벨링·학습 사용법");

    private void AppendLog(string message)
    {
        if (InvokeRequired) { BeginInvoke(() => AppendLog(message)); return; }
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\r\n");
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    private static string FindPython()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
            Path.Combine(local, "Python", "bin", "python.exe"),
            Path.Combine(local, "Python", "pythoncore-3.14-64", "python.exe"),
            Path.Combine(local, "Programs", "Python", "Python314", "python.exe"),
            Path.Combine(local, "Programs", "Python", "Python313", "python.exe"),
            "python"
        };
        return candidates.FirstOrDefault(File.Exists) ?? "python";
    }

    private static Label Label(string text) => new() { Text = text, Dock = DockStyle.Top, Height = 27, TextAlign = ContentAlignment.BottomLeft, Font = new Font("맑은 고딕", 9, FontStyle.Bold) };

    private static Control TwoFields(string firstTitle, Control first, string secondTitle, Control second)
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, Height = 65, ColumnCount = 2, RowCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.Controls.Add(Label(firstTitle), 0, 0);
        layout.Controls.Add(Label(secondTitle), 1, 0);
        layout.Controls.Add(first, 0, 1);
        layout.Controls.Add(second, 1, 1);
        return layout;
    }

    private static Panel Card(string title, Control content)
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(10) };
        panel.Controls.Add(content);
        panel.Controls.Add(new Label { Text = title, Dock = DockStyle.Top, Height = 34, Font = new Font("맑은 고딕", 11, FontStyle.Bold), ForeColor = Color.FromArgb(32, 45, 62) });
        return panel;
    }

    private static Button Button(string text, Action action, Color? back = null, Color? fore = null)
    {
        var button = new Button { Text = text, Dock = DockStyle.Fill, Height = 36, BackColor = back ?? Color.FromArgb(230, 235, 241), ForeColor = fore ?? Color.FromArgb(35, 45, 60), FlatStyle = FlatStyle.Flat, Margin = new Padding(3) };
        button.FlatAppearance.BorderSize = 0;
        button.Click += (_, _) => action();
        return button;
    }

    private static Button Button(string text, Func<Task> action, Color? back = null, Color? fore = null)
    {
        var button = Button(text, () => { }, back, fore);
        button.Click += async (_, _) => { button.Enabled = false; try { await action(); } finally { if (!button.IsDisposed) button.Enabled = true; } };
        return button;
    }

    private void ShowError(Exception exception) => MessageBox.Show(this, exception.Message, "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
}
