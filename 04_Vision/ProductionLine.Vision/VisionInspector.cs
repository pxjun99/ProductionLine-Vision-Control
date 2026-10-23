using ProductionLine.Shared;

namespace ProductionLine.Vision;

/// <summary>명시적으로 전달된 시뮬레이션 판정을 기록합니다. 실제 검사는 YOLO가 담당합니다.</summary>
public sealed class VisionInspector
{
    public InspectionDto Inspect(InspectionRequest request)
    {
        var valid = request.ImageAcquired && Enum.IsDefined(request.Result) && !string.IsNullOrWhiteSpace(request.Reason);
        return new InspectionDto(Guid.NewGuid(), request.LineId,
            valid ? request.Result : InspectionResultType.Undetermined,
            valid ? request.Reason.Trim() : "영상 또는 판정 결과를 확인할 수 없습니다.", DateTimeOffset.Now);
    }
}
