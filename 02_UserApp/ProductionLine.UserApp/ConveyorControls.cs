namespace ProductionLine.UserApp;

public sealed partial class UserMainForm
{
    private IConveyorController? _conveyor;
    private readonly DarkComboBox _conveyorPort = new() { Width = 104 };
    private readonly Button _conveyorConnect = new() { Text = "벨트 연결", AutoSize = true };
    private readonly Button _conveyorRun = new() { Text = "벨트 구동", AutoSize = true, Enabled = false };
    private readonly Label _lightState = new() { Text = "조명 자동 켜짐 · V5 이상(V7) 펌웨어가 설치된 아두이노에 USB 전원이 들어오면 켜집니다.", AutoSize = true, Padding = new Padding(0, 5, 0, 0) };
    private bool _lightCommandBusy;
    private readonly Label _conveyorState = new() { AutoSize = true, Text = "장치 미연결 · 아두이노 설정·업로드에서 연결을 준비하세요." };
    private readonly System.Windows.Forms.Timer _conveyorHeartbeat = new() { Interval = 500 };
    private bool _conveyorCommandBusy;
    private bool _conveyorHeartbeatBusy;
    private void InitializeConveyorControls()
    {
        _arduinoSetup.Click += async (_, _) => await OpenArduinoSetupAsync();
        _conveyorConnect.Click += async (_, _) => await ConnectConveyorAsync();
        _conveyorRun.Click += async (_, _) => await RunConveyorAsync();
        _conveyorHeartbeat.Tick += async (_, _) => await PingConveyorAsync();
        RefreshConveyorPorts();
    }

    private Control BuildConveyorControls()
    {
        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true, Margin = Padding.Empty };
        row.Controls.Add(new Label { Text = "아두이노 COM", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
        row.Controls.Add(_conveyorPort);
        row.Controls.Add(Button("포트 갱신", RefreshConveyorPorts));
        row.Controls.Add(_conveyorConnect);
        row.Controls.Add(Button("연결 해제", async () => await DisconnectConveyorAsync()));
        row.Controls.Add(_conveyorRun);
        return row;
    }

    private Control BuildInspectionLightControls()
    {
        var controls = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true, Margin = Padding.Empty };
        controls.Controls.Add(_lightState);
        return controls;
    }

    private void RefreshConveyorPorts()
    {
        if (_conveyor != null || _conveyorCommandBusy) return;
        try
        {
            var previous = _conveyorPort.SelectedItem as string ?? ArduinoPortSettings.Load();
            // v6 화면에는 이 목록이 없어 DataSource 바인딩이 채워지지 않습니다. Items에 직접 넣어 관리자 원격 목록이 항상 채워지게 합니다.
            _conveyorPort.DataSource = null;
            _conveyorPort.Items.Clear();
            _conveyorPort.Items.AddRange(ConveyorSerialController.GetPortNames().Cast<object>().ToArray());
            _conveyorPort.SelectedIndex = -1;
            if (previous != null && _conveyorPort.Items.Contains(previous)) _conveyorPort.SelectedItem = previous;
        }
        catch (Exception ex) { _conveyorState.Text = "COM 포트 조회 실패: " + ex.Message; }
    }

    private async Task ConnectConveyorAsync(bool forLighting = false)
    {
        if (_conveyor != null || _conveyorCommandBusy) return;
        if (forLighting && _conveyorPort.SelectedItem == null)
        {
            RefreshConveyorPorts();
            if (_conveyorPort.Items.Count == 1) _conveyorPort.SelectedIndex = 0;
        }
        if (_conveyorPort.SelectedItem is not string port)
        { _conveyorState.Text = "Mega 2560의 USB를 연결하고 COM 포트를 선택하세요. 처음에는 아두이노 설정·업로드를 누르세요."; return; }
        _conveyorCommandBusy = true;
        _conveyorConnect.Enabled = _conveyorPort.Enabled = false;
        _conveyorState.Text = "아두이노 연결 확인 중...";
        try
        {
            var controller = await ConveyorSerialController.ConnectAsync(port, stopMotorOnConnect: !forLighting);
            if (IsDisposed) { controller.Dispose(); return; }
            _conveyor = controller;
            ArduinoPortSettings.Save(port);
            _conveyorRun.Enabled = true;
            _topCom.Text = $"◇  {port} 연결";
            _topCom.ForeColor = IndustrialPalette.Green;
            _conveyorState.Text = forLighting ? $"{port} 조명 제어 연결됨"
                : $"{port} 연결됨 · 장치 상태 확인 완료.";
            _conveyorHeartbeat.Start();
            await PollHardwareAsync(controller);
        }
        catch (Exception ex) { if (!IsDisposed) _conveyorState.Text = "벨트 연결 실패: " + ex.Message; }
        finally
        {
            _conveyorCommandBusy = false;
            if (!IsDisposed) _conveyorConnect.Enabled = _conveyorPort.Enabled = _conveyor == null;
            UpdateInspectionLightControls();
        }
    }

    private async Task RunConveyorAsync()
    {
        if (_conveyor == null && !_conveyorCommandBusy) { _conveyorState.Text = "벨트 미연결 · COM 포트를 고르고 [벨트 연결]을 먼저 누르세요."; return; }
        if (_conveyor == null || _conveyorCommandBusy || _arduinoSetupBusy) return;
        if (_latestHardware is { Connected: true, EmergencyStopped: true })
        { _conveyorState.Text = "긴급 정지 잠금 중 · 버튼 원위치 → [잠금 해제] → 벨트 구동 순서로 다시 시작하세요."; return; }
        _conveyorCommandBusy = true;
        _conveyorRun.Enabled = false;
        try
        {
            await _conveyor.RunAsync();
            if (!IsDisposed) _conveyorState.Text = "PUL/DIR 구동 응답 확인 · 연속 검출 · 인식돼도 벨트는 계속 이동합니다.";
        }
        // 긴급 정지 잠금 때문에 거절된 것은 통신 고장이 아니므로 연결을 끊지 않습니다.
        catch (IOException ex) when (ex.Message.Contains("EMERGENCY_STOP"))
        { if (!IsDisposed) _conveyorState.Text = "긴급 정지 잠금 중 · 버튼 원위치 → [잠금 해제] → 벨트 구동 순서로 다시 시작하세요."; }
        catch (Exception ex) { if (!IsDisposed) FailConveyor("구동 응답 확인 실패: " + ex.Message); }
        finally { _conveyorCommandBusy = false; if (!IsDisposed) _conveyorRun.Enabled = _conveyor != null; }
    }

    private void UpdateInspectionLightControls()
    {
        if (IsDisposed) return;
        var light = _conveyor as IConveyorLightingController;
        _lightState.Text = _conveyor == null ? "조명 자동 켜짐 · V5 이상(V7) 펌웨어가 설치된 아두이노에 USB 전원이 들어오면 켜집니다."
            : light?.IsLightOn == null ? "조명 · 새 펌웨어 업로드 필요"
            : light.IsLightOn == true ? "흰색 조명 켜짐" : "조명 꺼짐";
    }

    private async Task SetInspectionLightAsync(bool enabled)
    {
        if (_lightCommandBusy) return;
        if (_conveyorCommandBusy || _arduinoSetupBusy)
        { _lightState.Text = "아두이노 연결·업로드 작업이 진행 중입니다."; return; }
        _lightCommandBusy = true;
        UpdateInspectionLightControls();
        try
        {
            if (_conveyor == null) await ConnectConveyorAsync(forLighting: true);
            if (IsDisposed) return;
            if (_conveyor is not IConveyorLightingController light)
            { _lightState.Text = "조명 연결 실패 · COM 포트와 USB를 확인하세요."; return; }
            _conveyorCommandBusy = true;
            await light.SetLightAsync(enabled);
        }
        catch (Exception ex)
        {
            if (!IsDisposed) _conveyorState.Text = "조명 제어 실패 · 아두이노 설정·업로드에서 D7 조명 코드를 한 번 올려주세요: " + ex.Message;
        }
        finally { _conveyorCommandBusy = false; _lightCommandBusy = false; UpdateInspectionLightControls(); }
    }

    private async Task StopConveyorAsync(string message)
    {
        var controller = _conveyor;
        if (controller == null) return;
        try
        {
            await controller.StopAsync();
            if (!IsDisposed && ReferenceEquals(controller, _conveyor)) _conveyorState.Text = message;
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
            {
                FailConveyor("벨트 정지 응답을 확인하지 못했습니다: " + ex.Message);
                StopInspection("정지 응답 확인 실패 · 벨트 실제 상태를 확인하세요.");
            }
            throw;
        }
    }

    private async Task PingConveyorAsync()
    {
        if (_conveyor == null || _conveyorHeartbeatBusy || _conveyorCommandBusy) return;
        var controller = _conveyor;
        _conveyorHeartbeatBusy = true;
        try
        {
            await controller.PingAsync();
            await PollHardwareAsync(controller);
        }
        catch (Exception ex)
        {
            if (!IsDisposed && ReferenceEquals(controller, _conveyor))
            {
                FailConveyor("아두이노 통신 오류 · 통신 중단 3초 후 정지: " + ex.Message);
                StopInspection("벨트 통신 오류로 검사 중지");
            }
        }
        finally { _conveyorHeartbeatBusy = false; }
    }

    private async Task DisconnectConveyorAsync()
    {
        if (_conveyorCommandBusy || _conveyor == null) return;
        var controller = _conveyor;
        _conveyorCommandBusy = true;
        _conveyorHeartbeat.Stop();
        var confirmed = false;
        try { await StopConveyorAsync("벨트 정지 응답 확인"); confirmed = true; }
        catch (Exception) { /* 연결 해제 후에도 펌웨어 통신 타임아웃이 적용됩니다. */ }
        finally
        {
            controller.Dispose();
            _conveyor = null;
            MarkHardwareDisconnected();
            _conveyorCommandBusy = false;
            if (!IsDisposed)
            {
                _conveyorRun.Enabled = false;
                _topCom.Text = "◇  COM 연결 확인";
                _topCom.ForeColor = Color.FromArgb(118, 178, 235);
                UpdateInspectionLightControls();
                _conveyorConnect.Enabled = _conveyorPort.Enabled = true;
                _conveyorState.Text = confirmed ? "벨트 정지 응답 확인 후 연결 해제" : "벨트 연결 해제 · 정지 응답 미확인 · 실제 상태를 확인하세요.";
            }
        }
    }

    private void FailConveyor(string message)
    {
        _conveyorHeartbeat.Stop();
        _conveyor?.Dispose();
        _conveyor = null;
        MarkHardwareDisconnected();
        _conveyorRun.Enabled = false;
        _topCom.Text = "◇  COM 연결 확인";
        _topCom.ForeColor = Color.FromArgb(118, 178, 235);
        UpdateInspectionLightControls();
        _conveyorConnect.Enabled = _conveyorPort.Enabled = true;
        _conveyorState.Text = message;
    }

}
