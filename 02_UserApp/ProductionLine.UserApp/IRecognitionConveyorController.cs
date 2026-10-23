namespace ProductionLine.UserApp;

internal interface IRecognitionConveyorController
{
    bool SupportsRecognitionBoost { get; }
    bool SupportsConfigurableBoost => false;
    Task RecognizeAsync(Guid productId, DateTime capturedUtc, int travelMs, int holdMs, bool sort, int boostMs = 3000);
}
