import argparse
import time
from pathlib import Path

import numpy as np
import onnx
import onnxruntime as ort
import torch
import torch.nn as nn
import torch.nn.functional as F
from PIL import Image
from torch.utils.data import DataLoader, Dataset

CLASS_NAMES = ["__", "wP", "wN", "wB", "wR", "wQ", "wK", "bP", "bN", "bB", "bR", "bQ", "bK"]


class SEBlock(nn.Module):
    def __init__(self, channels: int, reduction: int = 4):
        super().__init__()
        hidden = max(8, channels // reduction)
        self.fc1 = nn.Conv2d(channels, hidden, kernel_size=1)
        self.fc2 = nn.Conv2d(hidden, channels, kernel_size=1)

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        w = F.adaptive_avg_pool2d(x, 1)
        w = F.silu(self.fc1(w))
        w = torch.sigmoid(self.fc2(w))
        return x * w


class ResSEBlock(nn.Module):
    def __init__(self, in_ch: int, out_ch: int, stride: int = 1):
        super().__init__()
        self.conv1 = nn.Conv2d(in_ch, out_ch, kernel_size=3, stride=stride, padding=1, bias=False)
        self.bn1 = nn.BatchNorm2d(out_ch)
        self.conv2 = nn.Conv2d(out_ch, out_ch, kernel_size=3, stride=1, padding=1, bias=False)
        self.bn2 = nn.BatchNorm2d(out_ch)
        self.se = SEBlock(out_ch)

        if stride != 1 or in_ch != out_ch:
            self.shortcut = nn.Sequential(
                nn.Conv2d(in_ch, out_ch, kernel_size=1, stride=stride, bias=False),
                nn.BatchNorm2d(out_ch),
            )
        else:
            self.shortcut = nn.Identity()

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        out = F.silu(self.bn1(self.conv1(x)))
        out = self.bn2(self.conv2(out))
        out = self.se(out)
        return F.silu(out + self.shortcut(x))


class ChessPieceNet(nn.Module):
    def __init__(self, num_classes: int = 13):
        super().__init__()
        self.stem = nn.Sequential(
            nn.Conv2d(3, 32, kernel_size=3, stride=1, padding=1, bias=False),
            nn.BatchNorm2d(32),
            nn.SiLU(inplace=True),
        )
        self.stage1 = nn.Sequential(ResSEBlock(32, 48, stride=2), ResSEBlock(48, 48, stride=1))
        self.stage2 = nn.Sequential(ResSEBlock(48, 96, stride=2), ResSEBlock(96, 96, stride=1))
        self.stage3 = nn.Sequential(ResSEBlock(96, 160, stride=2), ResSEBlock(160, 160, stride=1))
        self.stage4 = nn.Sequential(ResSEBlock(160, 224, stride=2), ResSEBlock(224, 224, stride=1))
        self.head = nn.Sequential(
            nn.AdaptiveAvgPool2d(1),
            nn.Flatten(),
            nn.Dropout(p=0.25),
            nn.Linear(224, num_classes),
        )

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        x = self.stem(x)
        x = self.stage1(x)
        x = self.stage2(x)
        x = self.stage3(x)
        x = self.stage4(x)
        return self.head(x)


class NumpySquareDataset(Dataset):
    def __init__(self, images: np.ndarray, labels: np.ndarray, augment: bool = False):
        self.images = images
        self.labels = labels
        self.augment = augment

    def __len__(self) -> int:
        return len(self.labels)

    def __getitem__(self, idx: int) -> tuple[torch.Tensor, int]:
        img = torch.from_numpy(self.images[idx]).permute(2, 0, 1).float()
        img = (img - 127.5) / 127.5

        if self.augment:
            if torch.rand(1).item() < 0.35:
                gain = 0.90 + 0.20 * torch.rand(1).item()
                bias = -0.08 + 0.16 * torch.rand(1).item()
                img = torch.clamp(img * gain + bias, -1.0, 1.0)
            if torch.rand(1).item() < 0.25:
                img = torch.clamp(img + torch.randn_like(img) * 0.025, -1.0, 1.0)

        return img, int(self.labels[idx])


def load_real_holdout(holdout_dir: Path) -> NumpySquareDataset | None:
    if not holdout_dir.exists():
        return None

    imgs: list[np.ndarray] = []
    lbls: list[int] = []
    for sub in sorted(holdout_dir.iterdir()):
        if not sub.is_dir() or "_" not in sub.name:
            continue
        try:
            cls_idx = int(sub.name.split("_", 1)[0])
        except ValueError:
            continue
        if not (0 <= cls_idx < 13):
            continue
        for file in sorted(sub.glob("*.png")) + sorted(sub.glob("*.jpg")):
            im = Image.open(file).convert("RGB").resize((64, 64), Image.Resampling.BILINEAR)
            imgs.append(np.asarray(im, dtype=np.uint8))
            lbls.append(cls_idx)

    if not imgs:
        return None
    return NumpySquareDataset(np.stack(imgs, axis=0), np.array(lbls, dtype=np.uint8), augment=False)


@torch.no_grad()
def evaluate(model: nn.Module, loader: DataLoader, device: torch.device) -> tuple[float, float, np.ndarray]:
    model.eval()
    conf = np.zeros((13, 13), dtype=np.int64)
    for x, y in loader:
        x = x.to(device, non_blocking=True)
        logits = model(x)
        preds = logits.argmax(dim=1).cpu().numpy()
        targets = y.numpy()
        for t, p in zip(targets, preds):
            conf[t, p] += 1

    total = conf.sum()
    acc = float(np.trace(conf) / total) if total > 0 else 0.0

    f1s = []
    for c in range(13):
        tp = conf[c, c]
        fp = conf[:, c].sum() - tp
        fn = conf[c, :].sum() - tp
        if tp + fp + fn > 0:
            f1s.append((2.0 * tp) / (2.0 * tp + fp + fn))
    macro_f1 = float(np.mean(f1s)) if f1s else 0.0
    return acc, macro_f1, conf


def print_confusion_analytics(title: str, conf: np.ndarray) -> None:
    print(f"\n=== {title} (13x13 Confusion Matrix) ===")
    header = "     " + " ".join(f"{name:>4}" for name in CLASS_NAMES)
    print(header)
    for r, name in enumerate(CLASS_NAMES):
        row_vals = " ".join(f"{conf[r, c]:4d}" for c in range(13))
        print(f"{name:>4} {row_vals}")

    mistakes = []
    for r in range(13):
        for c in range(13):
            if r != c and conf[r, c] > 0:
                mistakes.append((int(conf[r, c]), CLASS_NAMES[r], CLASS_NAMES[c]))
    mistakes.sort(reverse=True)
    if mistakes:
        print("Top confused pairs:")
        for cnt, true_c, pred_c in mistakes[:10]:
            print(f"  {true_c} -> {pred_c}: {cnt}")
    else:
        print("Zero misclassifications on this split.")


def export_and_verify_onnx(model: nn.Module, onnx_path: Path) -> None:
    model_cpu = model.to("cpu").eval()
    onnx_path.parent.mkdir(parents=True, exist_ok=True)
    dummy = torch.randn(64, 3, 64, 64, dtype=torch.float32)

    torch.onnx.export(
        model_cpu,
        dummy,
        str(onnx_path),
        export_params=True,
        opset_version=17,
        do_constant_folding=True,
        input_names=["input"],
        output_names=["logits"],
        dynamic_axes={"input": {0: "batch_size"}, "logits": {0: "batch_size"}},
    )

    onnx.checker.check_model(str(onnx_path))

    with torch.no_grad():
        torch_out = model_cpu(dummy).numpy()

    sess_opts = ort.SessionOptions()
    sess_opts.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
    sess = ort.InferenceSession(str(onnx_path), sess_options=sess_opts, providers=["CPUExecutionProvider"])
    ort_out = sess.run(["logits"], {"input": dummy.numpy()})[0]
    max_diff = float(np.max(np.abs(torch_out - ort_out)))

    for _ in range(5):
        sess.run(["logits"], {"input": dummy.numpy()})
    t0 = time.perf_counter()
    runs = 30
    for _ in range(runs):
        sess.run(["logits"], {"input": dummy.numpy()})
    avg_ms = (time.perf_counter() - t0) * 1000.0 / runs

    size_mb = onnx_path.stat().st_size / (1024 * 1024)
    print(f"\nONNX exported: {onnx_path} ({size_mb:.2f} MB)")
    print(f"PyTorch vs ORT max abs logit diff: {max_diff:.6f}")
    print(f"CPU batch-64 full-board latency:   {avg_ms:.2f} ms")
    if max_diff > 1e-3:
        raise RuntimeError(f"ONNX numerical parity failed: max_diff={max_diff}")


def main() -> None:
    parser = argparse.ArgumentParser(description="Train ChessPieceNet and export ONNX for ChGhost")
    parser.add_argument("--data-dir", type=Path, default=Path("data/synth"))
    parser.add_argument("--holdout-dir", type=Path, default=Path("data/real_holdout/crops"))
    parser.add_argument("--out-onnx", type=Path, default=Path("../models/pieces_64x64.onnx"))
    parser.add_argument("--epochs", type=int, default=22)
    parser.add_argument("--batch-size", type=int, default=256)
    parser.add_argument("--lr", type=float, default=1.5e-3)
    args = parser.parse_args()

    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    print(f"Training device: {device}" + (f" ({torch.cuda.get_device_name(0)})" if device.type == "cuda" else ""))

    train_ds = NumpySquareDataset(
        np.load(args.data_dir / "train_images.npy"),
        np.load(args.data_dir / "train_labels.npy"),
        augment=True,
    )
    val_ds = NumpySquareDataset(
        np.load(args.data_dir / "val_images.npy"),
        np.load(args.data_dir / "val_labels.npy"),
        augment=False,
    )
    holdout_ds = load_real_holdout(args.holdout_dir)

    train_loader = DataLoader(train_ds, batch_size=args.batch_size, shuffle=True, num_workers=0, pin_memory=(device.type == "cuda"))
    val_loader = DataLoader(val_ds, batch_size=256, shuffle=False, num_workers=0, pin_memory=(device.type == "cuda"))
    holdout_loader = DataLoader(holdout_ds, batch_size=128, shuffle=False, num_workers=0) if holdout_ds else None

    model = ChessPieceNet(num_classes=13).to(device)
    param_count = sum(p.numel() for p in model.parameters())
    print(f"ChessPieceNet parameters: {param_count:,}")

    optimizer = torch.optim.AdamW(model.parameters(), lr=args.lr, weight_decay=2e-4)
    scheduler = torch.optim.lr_scheduler.CosineAnnealingLR(optimizer, T_max=args.epochs, eta_min=1e-5)
    criterion = nn.CrossEntropyLoss(label_smoothing=0.04)
    scaler = torch.amp.GradScaler("cuda", enabled=(device.type == "cuda"))

    best_metric = -1.0
    best_state = None
    best_val_conf = None
    best_holdout_conf = None

    for epoch in range(1, args.epochs + 1):
        t0 = time.perf_counter()
        model.train()
        loss_sum = 0.0
        seen = 0

        for x, y in train_loader:
            x = x.to(device, non_blocking=True)
            y = y.to(device, non_blocking=True)
            optimizer.zero_grad(set_to_none=True)

            with torch.amp.autocast("cuda", enabled=(device.type == "cuda")):
                logits = model(x)
                loss = criterion(logits, y)

            scaler.scale(loss).backward()
            scaler.step(optimizer)
            scaler.update()

            loss_sum += float(loss.item()) * len(y)
            seen += len(y)

        scheduler.step()
        train_loss = loss_sum / max(1, seen)
        val_acc, val_f1, val_conf = evaluate(model, val_loader, device)

        holdout_msg = ""
        metric = val_f1
        h_conf = None
        if holdout_loader is not None:
            h_acc, h_f1, h_conf = evaluate(model, holdout_loader, device)
            holdout_msg = f" | real_holdout acc={h_acc*100:.2f}% f1={h_f1*100:.2f}%"
            metric = h_f1

        dt = time.perf_counter() - t0
        print(
            f"Epoch {epoch:02d}/{args.epochs} ({dt:.1f}s) | "
            f"loss={train_loss:.4f} | synth_val acc={val_acc*100:.2f}% f1={val_f1*100:.2f}%{holdout_msg}"
        )

        if metric > best_metric:
            best_metric = metric
            best_state = {k: v.detach().cpu().clone() for k, v in model.state_dict().items()}
            best_val_conf = val_conf
            best_holdout_conf = h_conf

    assert best_state is not None
    model.load_state_dict(best_state)

    if best_val_conf is not None:
        print_confusion_analytics("Best Synthetic Val", best_val_conf)
    if best_holdout_conf is not None:
        print_confusion_analytics("Best REAL Holdout", best_holdout_conf)

    export_and_verify_onnx(model, args.out_onnx)


if __name__ == "__main__":
    main()
