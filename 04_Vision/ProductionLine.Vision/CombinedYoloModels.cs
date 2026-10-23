using OpenCvSharp;
using ProductionLine.Shared;
using System.Security.Cryptography;

namespace ProductionLine.Vision;

public sealed partial class YoloOnnxInspector
{
    private const string Iphone12ProMaxCrack = "iphone_12_pro_max_Crack";
    private readonly List<YoloOnnxInspector> _additionalModels = new();
    private string[]? _combinedNames;
    public IReadOnlyList<string> LoadedModelPaths => new[] { ModelPath }.Concat(_additionalModels.SelectMany(x => x.LoadedModelPaths)).ToArray();

    public static YoloOnnxInspector CreateCombined(float confidenceThreshold = 0.45f)
    {
        var combined = new YoloOnnxInspector(null, confidenceThreshold);
        try
        {
            var hashes = new HashSet<string> { Hash(combined.ModelPath) };
            var directory = Path.GetDirectoryName(combined.ModelPath)!;
            foreach (var path in Directory.EnumerateFiles(directory, "*.onnx", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
            {
                if (!hashes.Add(Hash(path))) continue;
                combined._additionalModels.Add(new YoloOnnxInspector(path, confidenceThreshold));
            }
            combined._combinedNames = new[] { combined._classNames }.Concat(combined._additionalModels.Select(x => x._classNames))
                .SelectMany(x => x)
                .SelectMany(name => name == Iphone12ProMaxCrack ? new[] { CrackPhone, Iphone12ProMax } : new[] { name })
                .Distinct(StringComparer.Ordinal).ToArray();
            return combined;
        }
        catch { combined.Dispose(); throw; }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string DetectionGroup(string name) => name is Crack or CrackPhone or CrackIphone ? name
        : IsSpecificPhoneClass(name) ? "iphone-model" : "phone";

    private YoloInspectionAnalysis InspectCombined(byte[] bytes)
    {
        using var image = Cv2.ImDecode(bytes, ImreadModes.Color);
        if (image.Empty()) return YoloInspectionAnalysis.Failed("이미지를 읽지 못했습니다.");

        if (IsEmptyConveyorFrame(image))
        {
            return new(false, InspectionResultType.Undetermined,
                "빈 컨베이어로 확인되어 휴대폰 검출을 대기합니다.", [], image.ToBytes(".jpg"));
        }

        // 카메라가 아직 영상을 보내지 않는 동안 생기는 검은 프레임은
        // 일부 구형 학습 모델에서 높은 신뢰도의 오검출을 만들 수 있습니다.
        Cv2.MeanStdDev(image, out var mean, out var deviation);
        if (mean.Val0 <= 5 && mean.Val1 <= 5 && mean.Val2 <= 5 &&
            deviation.Val0 <= 2 && deviation.Val1 <= 2 && deviation.Val2 <= 2)
        {
            return new(false, InspectionResultType.Undetermined,
                "카메라 영상이 비어 있어 검사를 기다리고 있습니다.", [], image.ToBytes(".jpg"));
        }

        var results = new List<(YoloOnnxInspector Model, YoloInspectionAnalysis Analysis)> { (this, InspectSingle(bytes)) };
        results.AddRange(_additionalModels.Select(model => (model, model.Inspect(bytes))));
        var classes = ClassNames.Select((name, index) => (name, index)).ToDictionary(x => x.name, x => x.index);
        // target_iphone과 crack은 구형 학습 모델의 내부 클래스이며 현재 검사 결과에는 사용하지 않습니다.
        // 정상 제품은 구체적인 기종·색상 클래스로, 불량은 crack_phone으로만 표시하고 판정합니다.
        var rawDetections = results.SelectMany(x => x.Analysis.Detections).ToArray();
        var trainedDamage = rawDetections.Any(x => x.ClassName == Iphone12ProMaxCrack);
        // 새 파손 전용 클래스는 파손 여부와 기종을 함께 뜻합니다.
        // 통합 결과에서는 공통 파손 이름과 기종으로 나누어 표시합니다.
        var detections = ApplyNms(rawDetections
            .SelectMany(x => x.ClassName == Iphone12ProMaxCrack
                ? new[] { x with { ClassName = CrackPhone }, x with { ClassName = Iphone12ProMax } }
                : new[] { x })
            .Where(x => x.ClassName is not TargetIphone and not Crack)
            .Select(x => x with { ClassId = classes[x.ClassName] }));
        var found = detections.Any(x => IsPhoneClass(x.ClassName));
        // 새 파손 전용 기종 클래스는 학습한 파손 결과를 사용합니다.
        // 기존 crack_phone 후보에는 기존 표면 무늬 검증을 유지합니다.
        var damageCandidate = detections.Any(x => x.ClassName == CrackPhone);
        var hasDamage = trainedDamage || (damageCandidate && HasVisibleSurfaceDamage(image, detections));
        var hasOtherPhone = detections.Any(x => x.ClassName == OtherPhone);
        var hasModel = detections.Any(x => IsSpecificPhoneClass(x.ClassName));
        var classified = results.Where(x => x.Model.CanClassifySingleModel && x.Analysis.ObjectFound).Select(x => x.Analysis).ToList();
        var normalQuality = classified.FirstOrDefault(x => x.Result == InspectionResultType.Normal);
        var result = hasDamage || hasOtherPhone ? InspectionResultType.Defect
            : hasModel || normalQuality != null ? InspectionResultType.Normal
            : InspectionResultType.Undetermined;
        var modelNames = string.Join(", ", detections.Where(x => IsSpecificPhoneClass(x.ClassName))
            .Select(x => DisplayClassName(x.ClassName)).Distinct());
        var reason = hasDamage
            ? $"crack_phone이 검출되어 불량으로 판정했습니다. 검출 기종: {(modelNames.Length > 0 ? modelNames : "기종 미확인")}."
            : hasOtherPhone
            ? "검사 대상이 아닌 휴대폰이 검출되어 불량으로 판정했습니다."
            : found
            ? $"검출 기종: {(modelNames.Length > 0 ? modelNames : "기종 미확인")}. " +
              (hasModel ? "휴대폰 전체에서 균열 무늬가 확인되지 않아 정상으로 판정했습니다."
                  : normalQuality?.Reason ?? "품질 판정 근거가 없어 판정 불가입니다.")
            : $"전체 학습 {LoadedModelPaths.Count}종에서 신뢰도 {ConfidenceThreshold:P0} 이상인 휴대폰을 찾지 못했습니다.";
        if (result == InspectionResultType.Defect)
        {
            // 깨짐 영역의 작은 박스 대신 휴대폰 전체 박스를 사용하고 crack_phone과 기종·색상을 함께 표시합니다.
            var wholePhone = detections.Where(x => IsPhoneClass(x.ClassName))
                .MaxBy(x => x.Box.Width * x.Box.Height);
            var damage = detections.Where(x => x.ClassName == CrackPhone)
                .MaxBy(x => x.Confidence);
            var display = wholePhone ?? damage;
            var model = detections.Where(x => IsSpecificPhoneClass(x.ClassName)).MaxBy(x => x.Confidence);
            var label = model == null ? CrackPhone : $"{CrackPhone} {model.ClassName}";
            if (display != null) DrawDetection(image, display with { ClassName = CrackPhone }, label);
        }
        else
        {
            // 정상일 때는 휴대폰 전체를 검출한 기종 박스 하나와 기종명만 표시합니다.
            var model = detections.Where(x => IsSpecificPhoneClass(x.ClassName))
                .MaxBy(x => x.Box.Width * x.Box.Height);
            if (model != null) DrawDetection(image, model);
        }
        return new(found, result, reason, detections, image.ToBytes(".jpg"));
    }

    private static bool HasVisibleSurfaceDamage(Mat image, IReadOnlyList<YoloDetection> detections)
    {
        var phone = detections.Where(x => IsSpecificPhoneClass(x.ClassName)).MaxBy(x => x.Confidence);
        if (phone == null) return false;

        var insetX = phone.Box.Width * .08f;
        var insetY = phone.Box.Height * .08f;
        var left = Math.Clamp((int)(phone.Box.X + insetX), 0, image.Width - 1);
        var top = Math.Clamp((int)(phone.Box.Y + insetY), 0, image.Height - 1);
        var right = Math.Clamp((int)(phone.Box.Right - insetX), left + 1, image.Width);
        var bottom = Math.Clamp((int)(phone.Box.Bottom - insetY), top + 1, image.Height);
        using var roi = new Mat(image, new Rect(left, top, right - left, bottom - top));
        using var gray = new Mat();
        using var blurred = new Mat();
        using var brightDetail = new Mat();
        using var mask = new Mat();
        Cv2.CvtColor(roi, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.GaussianBlur(gray, blurred, new Size(0, 0), 7);
        Cv2.Subtract(gray, blurred, brightDetail);
        Cv2.Threshold(brightDetail, mask, 24, 255, ThresholdTypes.Binary);
        var detailRatio = Cv2.CountNonZero(mask) / (double)(mask.Rows * mask.Cols);
        return detailRatio >= .04;
    }

    private Mat? LoadEmptyBeltReference()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Models", "background", "empty_belt_reference.jpg"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "models", "background", "empty_belt_reference.jpg"))
        };
        var path = candidates.FirstOrDefault(File.Exists);
        if (path == null) return null;

        using var source = Cv2.ImRead(path, ImreadModes.Grayscale);
        if (source.Empty()) return null;
        var reference = new Mat();
        Cv2.Resize(source, reference, new Size(320, 180), 0, 0, InterpolationFlags.Area);
        return reference;
    }

    private bool IsEmptyConveyorFrame(Mat image)
    {
        if (_emptyBeltReferenceGray == null) return false;

        using var gray = new Mat();
        using var resized = new Mat();
        using var difference = new Mat();
        using var changed = new Mat();
        Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.Resize(gray, resized, _emptyBeltReferenceGray.Size(), 0, 0, InterpolationFlags.Area);
        Cv2.Absdiff(resized, _emptyBeltReferenceGray, difference);

        // 컨베이어 중앙 영역만 비교해 주변 전선·바닥의 변화를 제외합니다.
        var roiRect = new Rect(10, 40, 300, 100);
        using var roi = new Mat(difference, roiRect);
        Cv2.Threshold(roi, changed, 25, 255, ThresholdTypes.Binary);
        var changedRatio = Cv2.CountNonZero(changed) / (double)(changed.Rows * changed.Cols);
        return changedRatio <= .03;
    }
}
