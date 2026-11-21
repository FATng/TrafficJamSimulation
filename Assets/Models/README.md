# Assets/Models

Thư mục này lưu trữ các mô hình trí tuệ nhân tạo định dạng ONNX phục vụ quá trình suy luận thời gian thực trên Unity 6 thông qua Unity Sentis.

---

## Mô hình mặc định: traffic_aif_agent.onnx

Mô hình được thiết kế theo kiến trúc mạng nơ-ron đa nhiệm, tích hợp giữa nhánh phân loại hành vi bậc cao và nhánh điều khiển liên tục.

### 1. Cấu trúc tensor và kiến trúc mạng

- Đầu vào (Input Tensor): Kích thước `[batch_size, 9]` gồm 9 thông số trạng thái chuẩn hóa:
  1. `normSpeed`: Vận tốc hiện tại của xe (v / 30.0).
  2. `normGap`: Cự ly tới vật cản gần nhất phía trước (d / 50.0).
  3. `normRelSpeed`: Vận tốc tương đối với vật cản phía trước (delta_v / 15.0).
  4. `speedError`: Sai số giữa vận tốc mong muốn và vận tốc hiện tại.
  5. `normRoadOffset`: Độ lệch vị trí xe so với tim đường.
  6. `sideBalance`: Độ chênh lệch khoảng trống giữa hai bên sườn xe.
  7. `isLeaderBraking`: Tín hiệu đèn phanh của xe dẫn đầu (1.0 nếu đang phanh, ngược lại 0.0).
  8. `pedestrianDetected`: Tín hiệu phát hiện người qua đường (1.0 nếu phát hiện, ngược lại 0.0).
  9. `trafficDensity`: Mật độ phương tiện cục bộ trên cung đường (0.0 đến 1.0).

- Đầu ra (Output Tensor): Kích thước `[batch_size, 7]` gồm hai phần:
  - Các chỉ số [0..4]: 5 điểm số hành vi đại diện cho 5 chế độ lái xe:
    - 0: FreeFlow (chạy tự do, đường thông thoáng)
    - 1: CarFollowing (bám đuôi xe trước)
    - 2: ActiveEvasion (chủ động lách né vật cản)
    - 3: SafeYielding (dừng nhường người qua đường)
    - 4: EmergencyShield (phanh khẩn cấp phòng tránh va chạm)
  - Chỉ số [5]: Gia tốc chuẩn hóa trong đoạn [-1.0, 1.0].
  - Chỉ số [6]: Góc đánh lái chuẩn hóa trong đoạn [-1.0, 1.0].

---

### 2. Cách sử dụng trong mã nguồn C# của Unity

Trong tập lệnh `AIFVehicleAgent.cs`:
```csharp
using Unity.InferenceEngine;

Model runtimeModel = ModelLoader.Load(onnxModelAsset);
Worker worker = new Worker(runtimeModel, BackendType.CPU);

float[] rawInputs = new float[] { normSpeed, normGap, normRelSpeed, speedError, normOffset, sideBalance, leaderBrake, pedAhead, density };
using var inputTensor = new Tensor<float>(new TensorShape(1, 9), rawInputs);

worker.Schedule(inputTensor);
var outputTensor = worker.PeekOutput() as Tensor<float>;
float[] results = outputTensor.DownloadToArray();

int bestMode = 0;
float maxLogit = results[0];
for (int i = 1; i < 5; i++)
{
    if (results[i] > maxLogit)
    {
        maxLogit = results[i];
        bestMode = i;
    }
}
DrivingMode currentMode = (DrivingMode)bestMode;
float accel = results[5] * maxAcceleration;
float steer = results[6] * 0.45f;
```

---

### 3. Huấn luyện và xuất lại mô hình

Tập lệnh huấn luyện Python đặt tại thư mục gốc dự án: `train_decision_agent.py`.

Thực thi lệnh tại thư mục gốc:
```bash
python train_decision_agent.py
```

Quy trình tự động:
1. Sinh 120.000 mẫu dữ liệu tình huống giao thông tổng hợp đa biến.
2. Huấn luyện mạng qua 12 lượt với thuật toán Adam (tốc độ học 0.003), kết hợp hàm mất mát phân loại và hồi quy.
3. Xuất mô hình trực tiếp sang tệp `traffic_aif_agent.onnx` với phiên bản toán tử 13 tương thích hoàn toàn với Unity Sentis.
