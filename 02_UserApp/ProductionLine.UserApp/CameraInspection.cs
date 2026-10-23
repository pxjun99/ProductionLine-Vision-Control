using ProductionLine.Shared;
using ProductionLine.Vision;

namespace ProductionLine.UserApp;

public sealed partial class UserMainForm
{
    private ICameraFeed? _camera;
    private Task _cameraClosed = Task.CompletedTask;
    private readonly DarkNumericUpDown _cameraIndex = new() { Minimum = 0, Maximum = 9, Width = 64 };
    private readonly Button _cameraConnect = new() { Text = "카메라 연결", AutoSize = true };
    private readonly Button _inspectionStart = new() { Text = "검사 시작", AutoSize = true };
    private readonly Label _inspectionState = new() { AutoSize = true, Text = "검사 대기 · 카메라 연결 후 YOLO 모델을 확인하세요.", Padding = new Padding(8) };
    private const int InspectionIntervalMs = 100;
    private readonly System.Windows.Forms.Timer _inspectionTimer = new() { Interval = InspectionIntervalMs };
    private YoloOnnxInspector? _yoloInspector;
    private string? _selectedYoloModelPath;
    private const float PhoneConfidenceThreshold = 0.45f;
    private long _displayedFrame, _inspectedFrame;
    private bool _runningInspection, _inspectionBusy;
    private bool _productPresent;
    private DateTime _lastProductSeenUtc;
    private int _inspectionGeneration;
    private readonly PictureBox _recognizedVideo = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(20, 30, 42) };
    private readonly Label _recognizedCaption = new() { Dock = DockStyle.Fill, Text = "마지막 인식 제품 · 대기 중", AutoEllipsis = true };
    private int _recognizedProductNumber;
    private bool _cameraControlsInitialized;

    private Control RecognitionVideoPanels()
    {
        var panels = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        panels.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 64)); panels.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
        var camera = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty };
        camera.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); camera.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        _videoStatus.Font = new Font("Noto Sans KR", 8.5f); _videoStatus.ForeColor = UiMuted;
        camera.Controls.Add(_video, 0, 0); camera.Controls.Add(_videoStatus, 0, 1);
        var live = Surface("실시간 카메라", camera); live.Margin = new Padding(0, 0, 10, 10); panels.Controls.Add(live, 0, 0);
        var product = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty };
        product.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); product.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        _recognizedCaption.Font = new Font("Noto Sans KR", 9); _recognizedCaption.ForeColor = UiMuted;
        product.Controls.Add(_recognizedVideo, 0, 0); product.Controls.Add(_recognizedCaption, 0, 1);
        var latest = Surface("최근 인식 제품", product); latest.Margin = new Padding(0, 0, 10, 10); panels.Controls.Add(latest, 1, 0); return panels;
    }

    private void ResetRecognizedResult()
    {
        _recognizedVideo.Image?.Dispose();
        _recognizedVideo.Image = null;
        _recognizedProductNumber = 0;
        _recognizedCaption.Text = "마지막 인식 제품 · 대기 중";
        _result.Text = "인식 대기 중 · 휴대폰이 인식되면 결과가 표시됩니다.";
        _result.ForeColor = UserTheme.Text;
    }

    private void InitializeCameraControls()
    {
        if (_cameraControlsInitialized) return;
        _cameraControlsInitialized = true;
        InitializeConveyorControls();
        _cameraConnect.Click += (_, _) => ConnectCamera();
        _inspectionStart.Click += async (_, _) => await StartInspectionAsync();
        _inspectionTimer.Tick += async (_, _) => await InspectCameraAsync();
    }

    private Control BuildCameraOperationControls()
    {
        InitializeCameraControls();
        var container = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty };
        container.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        container.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = false };
        buttons.Controls.Add(new Label { Text = "카메라", AutoSize = true, Padding = new Padding(0, 8, 2, 0) });
        buttons.Controls.Add(_cameraIndex); buttons.Controls.Add(_cameraConnect); buttons.Controls.Add(Button("연결 해제", DisconnectCamera));
        buttons.Controls.Add(_inspectionStart); buttons.Controls.Add(Button("검사 중지", () => StopInspection("검사 중지 · 전송 중인 1건은 완료될 수 있습니다.")));
        container.Controls.Add(buttons, 0, 0);
        _inspectionState.Font = new Font("Noto Sans KR", 9); _inspectionState.ForeColor = UiMuted; _inspectionState.Dock = DockStyle.Fill;
        container.Controls.Add(_inspectionState, 0, 1);
        return container;
    }

    private Control BuildLiveOperationControls()
    {
        InitializeCameraControls();
        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty, Padding = new Padding(6, 6, 0, 0) };
        row.Controls.Add(new Label { Text = "카메라", AutoSize = true, ForeColor = UiMuted, Padding = new Padding(0, 8, 2, 0) });
        row.Controls.Add(_cameraIndex); row.Controls.Add(_cameraConnect); row.Controls.Add(Button("연결 해제", DisconnectCamera));
        row.Controls.Add(_inspectionStart); row.Controls.Add(Button("검사 중지", () => StopInspection("검사 중지 · 전송 중인 1건은 완료될 수 있습니다.")));
        // 벨트 구동은 기존처럼 벨트 연결 전에는 눌리지 않는 버튼을 씁니다.
        _conveyorRun.Text = "벨트 구동"; row.Controls.Add(_conveyorRun);
        row.Controls.Add(Button("벨트 정지", async () => await StopConveyorAsync("벨트 정지 응답 확인")));
        _remoteControlAreas.Add(row);
        // 검사 상태(모델 준비·검사 시작 실패 이유)와 관리자 원격 알림 글자를 다시 표시합니다.
        var statuses = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        statuses.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65)); statuses.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        foreach (var label in new[] { _inspectionState, _remoteNotice })
        {
            label.AutoSize = false; label.Dock = DockStyle.Fill; label.AutoEllipsis = true; label.Padding = new Padding(8, 0, 4, 0);
            label.TextAlign = ContentAlignment.MiddleLeft; label.Font = new Font("Noto Sans KR", 9); label.ForeColor = UiMuted;
        }
        statuses.Controls.Add(_inspectionState, 0, 0); statuses.Controls.Add(_remoteNotice, 1, 0);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(row, 0, 0); layout.Controls.Add(statuses, 0, 1);
        return layout;
    }

    private Control BuildYoloSettingsControls()
    {
        InitializeCameraControls();
        var container = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Margin = Padding.Empty, Padding = new Padding(8) };
        container.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); container.RowStyles.Add(new RowStyle(SizeType.Absolute, 72)); container.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var models = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = false };
        models.Controls.Add(Button("모델 확인", CheckYoloModel));
        models.Controls.Add(Button("모델 선택", SelectYoloModel));
        container.Controls.Add(models, 0, 0); _remoteControlAreas.Add(models);
        container.Controls.Add(new Label { Dock = DockStyle.Fill, ForeColor = UiMuted, Text = "현재 검사 모델의 경로와 ONNX 입력 형식을 확인하거나 다른 모델을 선택합니다.\r\n검사 중에는 모델을 변경하지 않습니다." }, 0, 1);
        container.Controls.Add(new Label { Dock = DockStyle.Fill, ForeColor = UiMuted, Text = "검사 주기 100 ms · 휴대폰 검출 신뢰도 기준 45%\r\n결과는 서버 검사 이력과 제품 사진에 자동 저장됩니다." }, 0, 2);
        return container;
    }

    private Control BuildEquipmentControls()
    {
        InitializeCameraControls();
        var container = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 7, ColumnCount = 1, Margin = Padding.Empty };
        foreach (var height in new[] { 38, 38, 32, 36, 36, 40, 32 }) container.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        var setup = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = false };
        setup.Controls.Add(_arduinoSetup);
        setup.Controls.Add(new Label { Text = "Mega 2560 V7 펌웨어 · 센서/조명/벨트/분류 통합", AutoSize = true, Padding = new Padding(6, 8, 0, 0), ForeColor = UiMuted });
        container.Controls.Add(setup, 0, 0);
        container.Controls.Add(BuildConveyorControls(), 0, 2);
        _conveyorState.Dock = DockStyle.Fill;
        _conveyorState.AutoSize = false;
        _conveyorState.AutoEllipsis = true;
        container.Controls.Add(_conveyorState, 0, 1);
        container.Controls.Add(BuildInspectionLightControls(), 0, 3); container.Controls.Add(BuildBoostControls(), 0, 4);
        container.Controls.Add(BuildSortingControls(), 0, 5);
        container.Controls.Add(new Label { Dock = DockStyle.Fill, ForeColor = UiMuted, Text = "긴급정지 해제 후에는 안전을 확인하고 벨트 구동을 다시 눌러야 합니다." }, 0, 6);
        return container;
    }

    private void ConnectCamera()
    {
        if (_camera != null) return;
        if (!_cameraClosed.IsCompleted) { _videoStatus.Text = "이전 카메라 연결을 해제하는 중입니다. 잠시 후 다시 연결하세요."; return; }
        _displayedFrame = _inspectedFrame = 0;
        _productPresent = false;
        _lastProductSeenUtc = default;
        ResetRecognizedResult();
        _camera = new CameraSession((int)_cameraIndex.Value);
        _cameraConnect.Enabled = false; _cameraIndex.Enabled = false;
        _videoStatus.Text = "카메라 연결 중...";
        _frames.Interval = 50;
    }

    private void DisconnectCamera()
    {
        StopInspection("검사 대기 · 카메라를 다시 연결하세요.");
        var old = _camera; _camera = null;
        if (old != null) { old.Dispose(); _cameraClosed = old.Completion; }
        _productPresent = false;
        _lastProductSeenUtc = default;
        _cameraConnect.Enabled = true; _cameraIndex.Enabled = true;
        _video.Image?.Dispose(); _video.Image = null;
        _videoStatus.Text = "카메라 연결 해제됨";
        _remoteVideoAt = null;
        _frames.Interval = 500;
    }

    private void DisplayCameraFrame()
    {
        if (_camera == null) return;
        if (_camera.Error is string error)
        {
            DisconnectCamera(); _videoStatus.Text = error; return;
        }
        var frame = _camera.Latest;
        if (frame == null) return;
        if ((DateTime.UtcNow - frame.ReceivedUtc).TotalSeconds > 3)
        {
            StopInspection("영상 수신 지연으로 검사 중지");
            _videoStatus.Text = "카메라 영상이 3초 이상 갱신되지 않았습니다."; return;
        }
        // 검사 중에는 YOLO 결과 프레임을 표시합니다. 원본 영상이 박스를 덮어쓰지 않게 합니다.
        if (_runningInspection) return;
        if (frame.Sequence == _displayedFrame) return;
        using var stream = new MemoryStream(frame.Jpeg);
        using var source = Image.FromStream(stream);
        var old = _video.Image; _video.Image = new Bitmap(source); old?.Dispose();
        _displayedFrame = frame.Sequence;
        _remoteVideoAt = new DateTimeOffset(DateTime.SpecifyKind(frame.ReceivedUtc, DateTimeKind.Utc));
        _videoStatus.Text = $"카메라 {_cameraIndex.Value} 연결됨 · {frame.ReceivedUtc.ToLocalTime():HH:mm:ss} · 프레임 {frame.Sequence}";
    }

    private void CheckYoloModel()
    {
        try
        {
            EnsureYoloInspector();
            var displayClasses = _yoloInspector!.ClassNames
                .Where(name => name is not YoloOnnxInspector.TargetIphone and not YoloOnnxInspector.OtherPhone and not YoloOnnxInspector.Crack)
                .Select(YoloOnnxInspector.DisplayClassName).Distinct();
            _inspectionState.Text = $"YOLO 모델 준비 완료 · 신뢰도 {PhoneConfidenceThreshold:P0} · {_yoloInspector.CapabilityDescription} · 표시 클래스: {string.Join(", ", displayClasses)}";
        }
        catch (Exception ex)
        {
            _inspectionState.Text = "YOLO 모델 준비 실패: " + ex.Message;
        }
    }

    private void SelectYoloModel()
    {
        if (_runningInspection || _inspectionBusy || _arduinoSetupBusy)
        { _inspectionState.Text = "검사를 중지한 후 모델을 선택하세요."; return; }
        using var dialog = new OpenFileDialog
        {
            Title = "검사에 사용할 YOLO ONNX 모델 선택",
            Filter = "YOLO ONNX 모델 (*.onnx)|*.onnx",
            InitialDirectory = Path.Combine(AppContext.BaseDirectory, "Models")
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { LoadYoloModel(dialog.FileName); CheckYoloModel(); }
        catch (Exception ex) { _inspectionState.Text = "모델 선택 실패 · 기존 모델 유지: " + ex.Message; }
    }

    private void LoadYoloModel(string modelPath)
    {
        if (_runningInspection || _inspectionBusy || _arduinoSetupBusy)
            throw new InvalidOperationException("검사를 중지한 후 모델을 변경하세요.");
        var next = new YoloOnnxInspector(modelPath, PhoneConfidenceThreshold);
        var old = _yoloInspector;
        _yoloInspector = next;
        _selectedYoloModelPath = next.ModelPath;
        old?.Dispose();
        _productPresent = false;
        _lastProductSeenUtc = default;
        _inspectedFrame = 0;
        ResetRecognizedResult();
    }

    private async Task StartInspectionAsync()
    {
        if (_runningInspection || _inspectionBusy || _arduinoSetupBusy) return;
        if (_latestHardware is { Connected: true, EmergencyStopped: true })
        { _inspectionState.Text = "긴급 정지 잠금을 먼저 해제하세요."; return; }
        if (_camera?.Latest == null)
        { _inspectionState.Text = "먼저 카메라를 연결하세요."; return; }
        if (_lines.SelectedItem is not LineStatusDto { IsReal: true })
        { _inspectionState.Text = "실제 검사 가능한 라인을 선택하세요."; return; }
        try { EnsureYoloInspector(); }
        catch (Exception ex) { _inspectionState.Text = "YOLO 모델 준비 실패: " + ex.Message; return; }
        _runningInspection = true; _inspectionStart.Enabled = false;
        if (_recognizedVideo.Image == null)
            _result.Text = "인식 대기 중 · 휴대폰이 인식되면 결과가 표시됩니다.";
        _productPresent = false; _lastProductSeenUtc = default;
        _inspectionGeneration++;
        _inspectionState.Text = "YOLO 연속 검사 중 · 목표 간격 0.1초 · 제품별 기록은 한 번 저장";
        _inspectionTimer.Start();
        await InspectCameraAsync();
    }

    private void StopInspection(string message)
    {
        _runningInspection = false; _inspectionGeneration++;
        _inspectionTimer.Stop(); _inspectionStart.Enabled = !_inspectionBusy;
        _inspectionState.Text = message;
    }

    private async Task InspectCameraAsync()
    {
        if (!_runningInspection || _inspectionBusy) return;
        var frame = _camera?.Latest;
        if (frame == null || (DateTime.UtcNow - frame.ReceivedUtc).TotalSeconds > 3)
        { StopInspection("영상이 없어 검사를 중지했습니다. 카메라 연결을 확인하세요."); return; }
        if (frame.Sequence == _inspectedFrame) return;
        _inspectedFrame = frame.Sequence;
        var line = _lineId;
        var generation = _inspectionGeneration;
        string? recognizedResult = null;
        _inspectionBusy = true;
        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var analysis = await Task.Run(() => _yoloInspector!.Inspect(frame.Jpeg));
            if (IsDisposed || !_runningInspection || generation != _inspectionGeneration) return;
            stopwatch.Stop();

            // 실시간 영상은 계속 갱신하지만 제품 결과는 새 휴대폰을 인식했을 때만 갱신합니다.
            ShowYoloResultImage(analysis.ResultImage);
            _remoteVideoAt = new DateTimeOffset(DateTime.SpecifyKind(frame.ReceivedUtc, DateTimeKind.Utc));
            _displayedFrame = frame.Sequence;
            var confidence = analysis.Detections.Select(value => value.Confidence).DefaultIfEmpty(0).Max();
            var detectionResult = analysis.ObjectFound && !_yoloInspector!.CanClassifyProducts ? "휴대폰 검출 완료 · 품질 판정 미지원" : ResultText(analysis.Result);
            _videoStatus.Text = $"카메라 {_cameraIndex.Value} · YOLO 처리 {stopwatch.ElapsedMilliseconds}ms · 목표 간격 {InspectionIntervalMs}ms · 프레임 {frame.Sequence}";
            if (IsDisposed || !_runningInspection || generation != _inspectionGeneration) return;
            if (!analysis.Detections.Any(value => YoloOnnxInspector.IsPhoneClass(value.ClassName)))
            {
                if (_lastProductSeenUtc == default || (DateTime.UtcNow - _lastProductSeenUtc).TotalMilliseconds >= 250)
                { _productPresent = false; }
                _inspectionState.Text = "YOLO 연속 검사 중 · 목표 간격 0.1초 · 휴대폰 감지 대기";
                return;
            }

            _lastProductSeenUtc = DateTime.UtcNow;
            _inspectionState.Text = "YOLO 연속 검사 중 · 목표 간격 0.1초 · 같은 제품은 중복 집계하지 않습니다.";
            if (_productPresent) return;
            var visiblePhones = analysis.Detections.Where(value =>
                YoloOnnxInspector.IsPhoneClass(value.ClassName) &&
                IsPhoneFullyInsideFrame(value.Box, _video.Image?.Size ?? Size.Empty)).ToArray();
            if (visiblePhones.Length == 0)
            {
                _inspectionState.Text = "휴대폰 전체 진입 대기 · 화면 가장자리에서 잘린 제품은 아직 기록하지 않습니다.";
                return;
            }
            _productPresent = true;

            if (IsDisposed || !_runningInspection || generation != _inspectionGeneration) return;

            using (var stream = new MemoryStream(analysis.ResultImage))
            using (var source = Image.FromStream(stream))
            {
                var previous = _recognizedVideo.Image;
                _recognizedVideo.Image = new Bitmap(source);
                previous?.Dispose();
            }
            _recognizedProductNumber++;
            // 기종_crack(예: iphone_8_crack)이 나오면 그 이름 하나만 기종으로 표시·저장합니다. (김하균)
            var crackModel = analysis.Detections.Where(value => YoloOnnxInspector.IsCrackModelClass(value.ClassName))
                .MaxBy(value => value.Confidence)?.ClassName;
            var detectedModels = crackModel != null ? new[] { crackModel }
                : visiblePhones.Where(value => YoloOnnxInspector.IsPhoneModelClass(value.ClassName))
                    .Select(value => YoloOnnxInspector.DisplayClassName(value.ClassName)).Distinct().ToArray();
            var modelText = detectedModels.Length > 0 ? string.Join(", ", detectedModels) : "기종 미확인";
            var damageName = analysis.Detections.Any(value => value.ClassName == YoloOnnxInspector.CrackIphone)
                ? YoloOnnxInspector.CrackIphone : YoloOnnxInspector.CrackPhone;
            var names = analysis.Result == InspectionResultType.Defect && crackModel == null
                ? $"{damageName} · {modelText}"
                : modelText;
            // 크기 표시는 카메라 모듈·깨진 부위 같은 작은 박스가 아니라 폰 전체 박스 기준입니다.
            var mainPhone = visiblePhones.MaxBy(value => value.Box.Width * value.Box.Height)!;
            confidence = mainPhone.Confidence;
            var sizeText = $"화면상 크기 {mainPhone.Box.Width:F0}×{mainPhone.Box.Height:F0}px";
            _recognizedCaption.Text = $"마지막 인식 제품 · #{_recognizedProductNumber:0000}";
            recognizedResult = $"인식 제품 #{_recognizedProductNumber:0000} · {names} · 최고 신뢰도 {confidence:P1}\r\n{detectionResult} · {sizeText} · 인식 {frame.ReceivedUtc.ToLocalTime():HH:mm:ss.fff}";
            _result.Text = recognizedResult + "\r\n서버 기록 저장 중";
            _result.ForeColor = analysis.Result == InspectionResultType.Defect ? UserTheme.Red : UserTheme.Text;

            // 분류 명령(시간이 중요)을 먼저 보내고, 그다음 판정 표시등(빨강·초록)을 켭니다.
            try { await HandleRecognizedConveyorAsync(frame.ReceivedUtc, analysis.Result); }
            finally { await ShowResultLampAsync(analysis.Result); }

            // 기종은 DB에도 남기고, 제품 1대마다 요청 번호를 붙여 같은 기록이 두 번 저장되지 않게 함 (김하균)
            var saved = await _api.SaveCameraInspectionAsync(new CameraInspectionRequest(line, analysis.Result, analysis.Reason,
                confidence, _yoloInspector!.CanClassifyProducts, CreateRecordImage(analysis.ResultImage),
                detectedModels.Length > 0 ? modelText : null, Guid.NewGuid()));
            if (!saved.HasImage) throw new InvalidOperationException("인식 사진이 저장되지 않았습니다. 사진 저장 기능이 포함된 최신 서버로 다시 실행하세요.");
            if (IsDisposed || generation != _inspectionGeneration) return;
            _result.Text = recognizedResult + "\r\n기록 저장 완료 · 다음 제품을 인식할 때까지 이 결과를 유지합니다.";
            _inspectionState.Text = $"YOLO 연속 검사 중 · 목표 간격 0.1초 · 제품 기록 저장 {DateTime.Now:HH:mm:ss}";
            await RefreshAsync(false);
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
            {
                if (recognizedResult != null)
                    _result.Text = recognizedResult + "\r\n기록 처리 오류 · 저장 여부는 검사 이력에서 확인하세요.";
                StopInspection("검사 중지 · 분석/저장 실패: " + ex.Message + " · 자동 재전송하지 않습니다.");
                if (_automaticSort.Checked && _conveyor != null)
                {
                    try { await StopConveyorAsync("자동 분류 오류 · 벨트 정지 · 제품 위치를 확인하세요."); }
                    catch (Exception) { }
                    _automaticSort.Checked = false;
                }

            }
        }
        finally
        {
            _inspectionBusy = false;
            if (!IsDisposed)
            {
                _inspectionStart.Enabled = !_runningInspection;
                _conveyorRun.Enabled = _conveyor != null && !_conveyorCommandBusy;
            }
            else _yoloInspector?.Dispose();
        }
    }

    private void EnsureYoloInspector()
    {
        _yoloInspector ??= new YoloOnnxInspector(_selectedYoloModelPath, PhoneConfidenceThreshold);
        _selectedYoloModelPath = _yoloInspector.ModelPath;
    }

    internal static bool IsPhoneFullyInsideFrame(RectangleF box, Size imageSize)
    {
        if (imageSize.Width <= 0 || imageSize.Height <= 0 || box.Width <= 0 || box.Height <= 0)
            return false;
        var marginX = Math.Max(2f, imageSize.Width * 0.005f);
        var marginY = Math.Max(2f, imageSize.Height * 0.005f);
        return box.Left >= marginX && box.Top >= marginY &&
               box.Right <= imageSize.Width - marginX && box.Bottom <= imageSize.Height - marginY;
    }

    private void ShowYoloResultImage(byte[] imageBytes)
    {
        if (imageBytes.Length == 0) return;
        using var stream = new MemoryStream(imageBytes);
        using var source = Image.FromStream(stream);
        var old = _video.Image;
        _video.Image = new Bitmap(source);
        old?.Dispose();
    }
}
