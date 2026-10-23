import argparse
import json
import shutil
from pathlib import Path


def main() -> None:
    parser = argparse.ArgumentParser(description="Train an iPhone inspection YOLO detector and export ONNX")
    parser.add_argument("--data", required=True)
    parser.add_argument("--model", default="yolo26n.pt")
    parser.add_argument("--epochs", type=int, default=100)
    parser.add_argument("--imgsz", type=int, default=640)
    parser.add_argument("--device", default=None)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()

    from ultralytics import YOLO

    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    train_arguments = {
        "data": str(Path(args.data).resolve()),
        "epochs": args.epochs,
        "imgsz": args.imgsz,
        "project": str(output / "runs"),
        "name": "iphone_detector",
        "exist_ok": True,
        "workers": 0,
        "save": True,
    }
    if args.device:
        train_arguments["device"] = args.device

    print(f"MODEL={args.model}", flush=True)
    print(f"DATA={train_arguments['data']}", flush=True)
    model = YOLO(args.model)
    results = model.train(**train_arguments)

    save_dir = Path(str(results.save_dir))
    best_source = save_dir / "weights" / "best.pt"
    if not best_source.exists():
        raise FileNotFoundError(f"학습 결과 best.pt를 찾지 못했습니다: {best_source}")

    best_target = output / "best.pt"
    shutil.copy2(best_source, best_target)
    print(f"BEST_PT={best_target}", flush=True)

    export_model = YOLO(str(best_target))
    exported = export_model.export(format="onnx", imgsz=args.imgsz, dynamic=False)
    exported_path = Path(str(exported))
    onnx_target = output / "best.onnx"
    if exported_path.resolve() != onnx_target.resolve():
        shutil.copy2(exported_path, onnx_target)
    print(f"BEST_ONNX={onnx_target}", flush=True)

    metadata = {
        "model": args.model,
        "data": train_arguments["data"],
        "epochs": args.epochs,
        "imgsz": args.imgsz,
        "device": args.device or "auto",
        "best_pt": str(best_target),
        "best_onnx": str(onnx_target),
    }
    (output / "training_result.json").write_text(json.dumps(metadata, ensure_ascii=False, indent=2), encoding="utf-8")
    print("TRAINING_AND_EXPORT_COMPLETED", flush=True)


if __name__ == "__main__":
    main()
