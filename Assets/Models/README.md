# Assets/Models

Thư mục này lưu trữ các mô hình trí tuệ nhân tạo định dạng **`.onnx`** (Open Neural Network Exchange) phục vụ suy luận thời gian thực qua **Unity Sentis** trong Unity 6.

---

## 🧠 Mô hình hiện tại: `traffic_aif_agent.onnx`

Mô hình là mạng nơ-ron học sâu đa nhiệm (Multi-Head Neural Network) kết hợp giữa **Bộ ra quyết định hành vi bậc cao (High-Level Decision Head)** và **Bộ chấp hành điều khiển liên tục (Continuous Control Head)**.

### 1. Cấu trúc Tensor & Kiến trúc Mạng
- **Đầu vào (Input Tensor)**: Kích thước `[1, 9]` gồm 9 thông số quan sát toàn diện chuẩn hóa:
  1. `normSpeed`: Vận tốc hiện tại của xe ($v / 30.0$, kẹp trong $[0.0, 1.0]$).
  2. `normGap`: Cự ly tới vật cản phía trước ($d / 50.0$, kẹp trong $[0.0, 1.0]$).
  3. `normRelSpeed`: Vận tốc tương đối với vật cản ($\Delta v / 15.0$, kẹp trong $[-1.0, 1.0]$).
  4. `speedError`: Sai số giữa vận tốc mục tiêu và vận tốc hiện tại ($(v_{\text{target}} - v) / v_{\text{target}}$, kẹp trong $[-1.0, 1.0]$).
  5. `normRoadOffset`: Độ lệch vị trí so với tim đường ($x / \text{halfRoadWidth}$, kẹp trong $[-1.0, 1.0]$).
  6. `sideBalance`: Cân bằng cự ly hai bên sườn ($(distRight - distLeft) / 5.0$, kẹp trong $[-1.0, 1.0]$).
  7. `isLeaderBraking`: Đèn phanh xe dẫn đầu ($1.0$ nếu đang phanh, $0.0$ nếu không).
  8. `pedestrianDetected`: Nhận diện người đi bộ phía trước ($1.0$ nếu có, $0.0$ nếu không).
  9. `trafficDensity`: Mật độ giao thông tại vị trí xe từ `TrafficFieldManager` ($[0.0, 1.0]$).

- **Đầu ra (Output Tensor)**: Kích thước `[1, 7]` gồm:
  - **TẦNG 1: Decision Logits `[0..4]` (5 lớp hành vi `DrivingMode`)**:
    - `Output[0]`: Điểm số hành vi `FreeFlow` (Đường thoáng, chạy tự do)
    - `Output[1]`: Điểm số hành vi `CarFollowing` (Bám đuôi xe trước)
    - `Output[2]`: Điểm số hành vi `ActiveEvasion` (Chủ động lách né vật cản)
    - `Output[3]`: Điểm số hành vi `SafeYielding` (Dừng nhường người qua đường)
    - `Output[4]`: Điểm số hành vi `EmergencyShield` (Phanh khẩn cấp & chống va chạm)
    - *Xử lý trong C#*: `argmax(Output[0..4])` chọn trực tiếp `DrivingMode`.
  - **TẦNG 2: Continuous Controls `[5..6]` (Chấp hành vật lý)**:
    - `Output[5]`: Gia tốc chuẩn hóa $[-1.0, 1.0] \rightarrow a = \text{Output}[5] \times \text{maxAcceleration}$.
    - `Output[6]`: Góc bẻ lái chuẩn hóa $[-1.0, 1.0] \rightarrow \text{steer} = \text{Output}[6] \times 0.45\text{ rad}$.

---

### 2. Cách nạp và sử dụng với Unity Sentis trong C#
Trong script [`AIFVehicleAgent.cs`](../Scripts/AIFVehicleAgent.cs):
```csharp
using Unity.InferenceEngine;

// 1. Tự động load ModelAsset
Model runtimeModel = ModelLoader.Load(onnxModelAsset);
Worker worker = new Worker(runtimeModel, BackendType.CPU);

// 2. Tạo Input Tensor và chạy suy luận (Inference)
float[] rawInputs = new float[] { normSpeed, normGap, normRelSpeed, speedError, normOffset, sideBalance, leaderBrake, pedAhead, density };
using var inputTensor = new Tensor<float>(new TensorShape(1, 9), rawInputs);

worker.Schedule(inputTensor);
var outputTensor = worker.PeekOutput() as Tensor<float>;
float[] results = outputTensor.DownloadToArray();

// 3. Giải mã kết quả phân tầng
int bestMode = 0; float maxLogit = results[0];
for (int i = 1; i < 5; i++) {
    if (results[i] > maxLogit) { maxLogit = results[i]; bestMode = i; }
}
DrivingMode currentMode = (DrivingMode)bestMode;
float accel = results[5] * maxAcceleration;
float steer = results[6] * 0.45f;
```
