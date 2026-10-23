using ProductionLine.Shared;

namespace ProductionLine.UserApp;

public sealed partial class UserMainForm
{
    internal const int FixedSortTravelMs = 15000;
    internal const int FixedSortHoldMs = 7000;
    private const int HardwareLineId = 1;
    private UpdateLineHardwareRequest? _latestHardware;
    private DateTimeOffset _latestHardwareAt;
    private bool _hardwarePublishBusy;
    private DateTimeOffset _lastHardwarePublish;
    private readonly NumericUpDown _boostSeconds = new() { Minimum = 0.5m, Maximum = 60, DecimalPlaces = 1, Increment = 0.5m, Value = 18, Width = 80 };
    private readonly CheckBox _automaticSort = new() { Text = "불량 자동 분류", AutoSize = true, Checked = true };
    private readonly NumericUpDown _sortTravelMs = new() { Minimum = 500, Maximum = 60000, Value = FixedSortTravelMs, Width = 76 };
    private readonly NumericUpDown _sortHoldMs = new() { Minimum = 200, Maximum = 60000, Value = FixedSortHoldMs, Width = 76 };
    private readonly Label _sortTimingSummary = new() { AutoSize = true, Padding = new Padding(8, 7, 12, 0), ForeColor = UiMuted };

    private void UpdateSortTimingSummary() => _sortTimingSummary.Text =
        $"기본속도 이동 {_sortTravelMs.Value:N0}ms  ·  55° 유지시간 {_sortHoldMs.Value:N0}ms · 관리자 설정";
    private readonly Label _hardwareSummary = new()
    {
        Dock = DockStyle.Fill, Text = "온도 --℃ / 습도 --% / 가스 상태 미확인 · 아두이노 연결 필요",
        Padding = new Padding(8), AutoEllipsis = true
    };

    private Control BuildBoostControls()
    {
        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true, Margin = Padding.Empty };
        row.Controls.Add(new Label { Text = "인식 후 2배 가속 · 유지 시간(초)", AutoSize = true, Padding = new Padding(0,6,0,0) });
        row.Controls.Add(_boostSeconds);
        row.Controls.Add(new Label { Text = "다음 제품 인식부터 적용 · V7 업로드 필요", AutoSize = true, Padding = new Padding(0,6,0,0) });
        return row;
    }
    private Control BuildSortingControls()
    {
        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true, Margin = Padding.Empty };
        row.Controls.Add(_automaticSort);
        UpdateSortTimingSummary();
        row.Controls.Add(_sortTimingSummary);
        row.Controls.Add(Button("긴급 정지 잠금 해제", async () => await ResetEmergencyAsync()));
        return row;
    }

    private async Task PollHardwareAsync(IConveyorController controller)
    {
        if (controller is not IHardwareConveyorController { SupportsHardware: true } hardware)
        {
            _hardwareSummary.Text = "센서·분류 장비 대기 · 아두이노 설정·업로드에서 V4 코드를 올리세요.";
            return;
        }
        var status = await hardware.ReadHardwareAsync();
        if (IsDisposed || !ReferenceEquals(controller, _conveyor)) return;
        _latestHardware = status; _latestHardwareAt = DateTimeOffset.UtcNow;
        ShowLocalHardware(status);
        if (status.EmergencyStopped)
        {
            _automaticSort.Checked = false;
            StopInspection("D12 긴급 정지 작동 · 물리 버튼 해제 → 잠금 해제 → 수동 벨트 구동");
        }
        if (!_hardwarePublishBusy && DateTimeOffset.UtcNow - _lastHardwarePublish >= TimeSpan.FromSeconds(1))
            _ = PublishHardwareAsync(status);
    }

    private void ShowLocalHardware(UpdateLineHardwareRequest status)
    {
        var dto = new LineHardwareStatusDto(status.Connected, status.TemperatureCelsius, status.HumidityPercent,
            status.GasDetected, status.EmergencyStopped, status.BeltRunning, status.SorterAngle, status.Sorting, _latestHardwareAt, status.SpeedBoosted, status.GasAnalogValue, status.GasWarmingUp);
        _hardwareSummary.Text = HardwareStatusText.Format(dto);
        _sensorTrends.Append(dto);
        UpdateLiveHardware(dto);
        _hardwareSummary.ForeColor = status.EmergencyStopped || status.GasDetected == true ? Color.Firebrick : UserTheme.Text;
    }

    private async Task PublishHardwareAsync(UpdateLineHardwareRequest status)
    {
        _hardwarePublishBusy = true; _lastHardwarePublish = DateTimeOffset.UtcNow;
        try { await _api.UpdateLineHardwareAsync(HardwareLineId, status); }
        catch (Exception)
        {
            if (!IsDisposed) _hardwareSummary.Text += " · 서버 공유 실패";
        }
        finally { _hardwarePublishBusy = false; }
    }

    private async Task ResetEmergencyAsync()
    {
        if (_conveyorCommandBusy || _arduinoSetupBusy) return;
        if (_conveyor is not IHardwareConveyorController { SupportsHardware: true } hardware)
        { _conveyorState.Text = "먼저 센서·긴급정지 V4 펌웨어로 아두이노를 연결하세요."; return; }
        if ((_conveyor as ConveyorSerialController)?.EmergencySwitchReady == false)
        { _conveyorState.Text = "긴급 정지 버튼이 아직 눌려 있습니다 · 버튼을 원위치한 뒤 [잠금 해제]를 누르세요."; return; }
        _conveyorCommandBusy = true;
        try
        {
            await hardware.ResetEmergencyAsync();
            _conveyorState.Text = "긴급 정지 잠금 해제 확인 · 자동 재출발하지 않습니다. 위치 확인 후 관리자 원격 제어로 벨트를 구동하세요.";
            await PollHardwareAsync(_conveyor);
        }
        catch (Exception ex) { if (!IsDisposed) _conveyorState.Text = "긴급 정지 해제 실패: " + ex.Message; }
        finally { _conveyorCommandBusy = false; }
    }

    private async Task QueueDefectSortingAsync(DateTime capturedUtc)
    {
        if (!_automaticSort.Checked) return;
        if (_conveyor is not IHardwareConveyorController { SupportsHardware: true } hardware ||
            _latestHardware is not { Connected: true, EmergencyStopped: false, BeltRunning: true } ||
            DateTimeOffset.UtcNow - _latestHardwareAt > TimeSpan.FromSeconds(2))
            throw new InvalidOperationException("자동 분류 장비의 구동 상태를 확인할 수 없습니다.");
        var due = new DateTimeOffset(DateTime.SpecifyKind(capturedUtc, DateTimeKind.Utc)).AddMilliseconds((int)_sortTravelMs.Value);
        await hardware.QueueSortAsync(Guid.NewGuid(), due, (int)_sortHoldMs.Value);
        if (!IsDisposed) _conveyorState.Text = $"불량 분류 예약 확인 · D11 55° → {_sortHoldMs.Value / 1000m:0.###}초 유지 → 0° 복귀";
    }

    private void MarkHardwareDisconnected()
    {
        _automaticSort.Checked = false;
        if (_latestHardware is { } last)
        {
            _latestHardware = last with { Connected = false, BeltRunning = false, Sorting = false, SpeedBoosted = false };
            _sensorTrends.Append(new LineHardwareStatusDto(false, null, null, null, last.EmergencyStopped, false, last.SorterAngle, false, DateTimeOffset.UtcNow));
            _hardwareSummary.Text = "장비 통신 끊김 · 온습도·가스·분류 상태 미확인";
            UpdateLiveHardware(null);
            // Server's 5-second expiry also covers a failed offline notification.
            if (!_hardwarePublishBusy) _ = PublishHardwareAsync(_latestHardware);
        }
    }
    // 판정 표시등은 보조 표시라서 실패해도 검사·분류·기록 저장을 막지 않습니다.
    private async Task ShowResultLampAsync(InspectionResultType result)
    {
        if (result is not (InspectionResultType.Normal or InspectionResultType.Defect)) return;
        if (_conveyor is not ConveyorSerialController serial || !serial.SupportsHardware) return;
        try { await serial.SetResultLampAsync(result == InspectionResultType.Defect); }
        catch (Exception) { }
    }

    private async Task HandleRecognizedConveyorAsync(DateTime capturedUtc, InspectionResultType result)
    {
        var shouldSort = _automaticSort.Checked && result == InspectionResultType.Defect;
        if (_conveyor is IRecognitionConveyorController { SupportsRecognitionBoost: true } recognition)
        {
            if (_conveyor.IsRunning != true)
            {
                if (shouldSort) throw new InvalidOperationException("불량 자동 분류를 위해 벨트를 먼저 구동하세요.");
                _conveyorState.Text = "제품 인식 저장 · 벨트가 정지 중이므로 자동 출발·가속하지 않습니다.";
                return;
            }
            if (shouldSort && (_latestHardware is not { Connected: true, EmergencyStopped: false } ||
                DateTimeOffset.UtcNow - _latestHardwareAt > TimeSpan.FromSeconds(2)))
                throw new InvalidOperationException("자동 분류 장비의 최신 상태를 확인할 수 없습니다.");
            var duration = recognition.SupportsConfigurableBoost ? (int)(_boostSeconds.Value * 1000) : 3000;
            var gain = recognition.SupportsConfigurableBoost ? "2배" : "약 1.25배 · 시간 변경은 V7 업로드 필요";
            await recognition.RecognizeAsync(Guid.NewGuid(),capturedUtc,(int)_sortTravelMs.Value,(int)_sortHoldMs.Value,shouldSort,duration);
            if (!IsDisposed) _conveyorState.Text = shouldSort
                ? $"불량 인식 · {duration/1000d:0.#}초 {gain} 가속 · 실제 이동량에 맞춰 55° 분류 → 0° 복귀"
                : $"제품 인식 · {duration/1000d:0.#}초 동안 {gain} 가속 후 원래 속도 복귀";
        }
        else
        {
            if (result == InspectionResultType.Defect) await QueueDefectSortingAsync(capturedUtc);
            if (_conveyor != null) _conveyorState.Text = "기존 검사는 유지됩니다. 인식 후 가속은 아두이노 설정·업로드에서 V7 코드를 올리세요.";
        }
    }
}
