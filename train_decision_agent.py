import torch
import torch.nn as nn
import torch.optim as optim
import numpy as np
import os

# Cố định random seed để kết quả tái lập được 100%
torch.manual_seed(42)
np.random.seed(42)

# ==============================================================================
# 1. ĐỊNH NGHĨA KIẾN TRÚC MẠNG NƠ-RON PHÂN TẦNG ĐA NHIỆM (HIERARCHICAL POLICY)
# ==============================================================================
class TrafficHierarchicalPolicy(nn.Module):
    def __init__(self, input_dim=9, hidden_dim=64, num_classes=5, control_dim=2):
        super().__init__()
        # Lớp trích xuất đặc trưng chung (Shared Feature Extractor)
        self.shared = nn.Sequential(
            nn.Linear(input_dim, hidden_dim),
            nn.ReLU(),
            nn.Linear(hidden_dim, hidden_dim),
            nn.ReLU()
        )
        # Tầng 1: Nhánh phân loại ra quyết định hành vi bậc cao (5 lớp DrivingMode)
        self.decision_head = nn.Linear(hidden_dim, num_classes)
        # Tầng 2: Nhánh điều khiển liên tục sinh Gia tốc & Góc lái
        self.control_head = nn.Sequential(
            nn.Linear(hidden_dim, control_dim),
            nn.Tanh()
        )

    def forward(self, x):
        features = self.shared(x)
        logits = self.decision_head(features)
        control = self.control_head(features)
        # Ghép thành 1 Tensor đầu ra [batch, 7] để Unity Sentis suy luận trong 1 forward pass
        return torch.cat([logits, control], dim=-1)

def main():
    print("=" * 60)
    print("BẮT ĐẦU HUẤN LUYỆN MÔ HÌNH HỌC SÂU ĐIỀU KHIỂN XE (TRAFFIC AIF AGENT)")
    print("=" * 60)

    # ==============================================================================
    # 2. SINH TẬP DỮ LIỆU HUẤN LUYỆN (120,000 TÌNH HUỐNG GIAO THÔNG ĐA BIẾN)
    # ==============================================================================
    N = 120000
    speed = np.random.uniform(0.0, 1.0, N)
    gap = np.random.uniform(0.02, 1.0, N)
    rel_speed = np.random.uniform(-1.0, 1.0, N)
    speed_error = np.random.uniform(-1.0, 1.0, N)
    road_offset = np.random.uniform(-1.0, 1.0, N)
    side_balance = np.random.uniform(-1.0, 1.0, N)
    leader_braking = np.random.choice([0.0, 1.0], N, p=[0.75, 0.25])
    pedestrian = np.random.choice([0.0, 1.0], N, p=[0.85, 0.15])
    density = np.random.uniform(0.0, 1.0, N)

    X = np.stack([speed, gap, rel_speed, speed_error, road_offset, side_balance, leader_braking, pedestrian, density], axis=1).astype(np.float32)

    # Gán nhãn quyết định Tầng 1 (0: FreeFlow, 1: CarFollowing, 2: ActiveEvasion, 3: SafeYielding, 4: EmergencyShield)
    decision = np.zeros(N, dtype=np.int64)
    emergency_mask = (gap < 0.08) | ((gap < 0.15) & (rel_speed < -0.4))
    decision[emergency_mask] = 4

    yield_mask = (~emergency_mask) & (pedestrian == 1.0) & (gap < 0.25)
    decision[yield_mask] = 3

    evasion_mask = (~emergency_mask) & (~yield_mask) & (gap < 0.35) & (np.abs(side_balance) > 0.20)
    decision[evasion_mask] = 2

    follow_mask = (~emergency_mask) & (~yield_mask) & (~evasion_mask) & (gap < 0.45)
    decision[follow_mask] = 1

    free_mask = (~emergency_mask) & (~yield_mask) & (~evasion_mask) & (~follow_mask)
    decision[free_mask] = 0

    # Mục tiêu điều khiển liên tục Tầng 2 [Gia tốc, Góc lái]
    target_accel = np.zeros(N, dtype=np.float32)
    target_steer = -0.35 * road_offset

    target_accel[decision == 0] = np.clip(0.8 * speed_error[decision == 0], -0.2, 1.0)
    target_accel[decision == 1] = np.clip(0.6 * rel_speed[decision == 1] - 0.3 * leader_braking[decision == 1], -0.8, 0.5)

    evade_dir = np.sign(side_balance[decision == 2])
    evade_dir = np.where(evade_dir == 0, 1.0, evade_dir)
    target_accel[decision == 2] = -0.15
    target_steer[decision == 2] = np.clip(target_steer[decision == 2] + 0.45 * evade_dir, -1.0, 1.0)

    target_accel[decision == 3] = -0.95
    target_steer[decision == 3] = 0.0

    target_accel[decision == 4] = -1.0
    target_steer[decision == 4] = 0.0

    Y_control = np.stack([target_accel, target_steer], axis=1).astype(np.float32)

    # ==============================================================================
    # 3. HUẤN LUYỆN MẠNG NƠ-RON
    # ==============================================================================
    model = TrafficHierarchicalPolicy(input_dim=9, hidden_dim=64, num_classes=5, control_dim=2)
    criterion_cls = nn.CrossEntropyLoss()
    criterion_reg = nn.MSELoss()
    optimizer = optim.Adam(model.parameters(), lr=0.003)

    dataset = torch.utils.data.TensorDataset(
        torch.from_numpy(X),
        torch.from_numpy(decision),
        torch.from_numpy(Y_control)
    )
    loader = torch.utils.data.DataLoader(dataset, batch_size=256, shuffle=True)

    model.train()
    epochs = 12
    for epoch in range(epochs):
        total_cls_loss = 0.0
        total_reg_loss = 0.0
        correct = 0
        total_samples = 0
        for bx, b_cls, b_reg in loader:
            optimizer.zero_grad()
            out = model(bx)
            logits = out[:, :5]
            controls = out[:, 5:]
            
            loss_cls = criterion_cls(logits, b_cls)
            loss_reg = criterion_reg(controls, b_reg)
            loss = loss_cls + 2.0 * loss_reg
            loss.backward()
            optimizer.step()
            
            total_cls_loss += loss_cls.item()
            total_reg_loss += loss_reg.item()
            preds = torch.argmax(logits, dim=-1)
            correct += (preds == b_cls).sum().item()
            total_samples += b_cls.size(0)

        acc = correct / total_samples * 100.0
        print(f"Epoch {epoch+1:02d}/{epochs} - Độ chính xác phân loại: {acc:.2f}% | Cls Loss: {total_cls_loss/len(loader):.4f} | Reg Loss: {total_reg_loss/len(loader):.4f}")

    # ==============================================================================
    # 4. XUẤT RA TỆP ONNX CHO UNITY SENTIS
    # ==============================================================================
    model.eval()
    dummy_input = torch.randn(1, 9, dtype=torch.float32)
    output_dir = os.path.join(os.path.dirname(__file__), "Assets", "Models")
    os.makedirs(output_dir, exist_ok=True)
    onnx_path = os.path.join(output_dir, "traffic_aif_agent.onnx")

    torch.onnx.export(
        model,
        dummy_input,
        onnx_path,
        export_params=True,
        opset_version=13,
        do_constant_folding=True,
        input_names=['input'],
        output_names=['output'],
        dynamic_axes={'input': {0: 'batch_size'}, 'output': {0: 'batch_size'}}
    )

    print("=" * 60)
    print(f"XUẤT MÔ HÌNH THÀNH CÔNG: {onnx_path}")
    print("=" * 60)

if __name__ == '__main__':
    main()
