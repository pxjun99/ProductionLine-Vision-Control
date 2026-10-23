namespace ProductionLine.UserApp;

public sealed partial class UserMainForm
{
    private readonly Button _arduinoSetup = new() { Text = "아두이노 설정·업로드", AutoSize = true };
    private bool _arduinoSetupBusy;

    private async Task OpenArduinoSetupAsync()
    {
        if (_arduinoSetupBusy || _conveyorCommandBusy || _inspectionBusy)
        { _conveyorState.Text = "진행 중인 검사나 명령이 끝난 뒤 아두이노 설정을 여세요."; return; }
        _arduinoSetupBusy = true;
        _arduinoSetup.Enabled = false;
        var port = _conveyorPort.SelectedItem as string;
        try
        {
            StopInspection("아두이노 설정 중 · 검사를 중지합니다.");
            if (_conveyor != null)
            {
                // A confirmed stop is required before handing the serial port to the uploader.
                await StopConveyorAsync("아두이노 설정을 위해 벨트 정지 응답 확인");
                await DisconnectConveyorAsync();
            }
            if (IsDisposed) return;
            using var setup = new ArduinoSetupForm(port);
            if (setup.ShowDialog(this) != DialogResult.OK || setup.UploadedPort is not string uploaded) return;
            RefreshConveyorPorts();
            if (!_conveyorPort.Items.Contains(uploaded))
            { _conveyorState.Text = "업로드는 성공했지만 포트가 아직 보이지 않습니다. 포트 갱신 후 벨트 연결을 누르세요."; return; }
            _conveyorPort.SelectedItem = uploaded;
            await ConnectConveyorAsync(); // Handshake + STOP only; never RUN after flashing.
        }
        catch (Exception ex) { if (!IsDisposed) _conveyorState.Text = "아두이노 설정 실패: " + ex.Message; }
        finally
        {
            _arduinoSetupBusy = false;
            if (!IsDisposed) _arduinoSetup.Enabled = true;
            UpdateInspectionLightControls();
        }
    }
}
