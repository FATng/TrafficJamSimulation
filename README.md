# Traffic Jam Simulation (Unity 6 + Sentis AI)

> **Mô phỏng dòng giao thông thông minh và hiện tượng kẹt xe (Phantom Jam) trên Unity 6**, kết hợp giữa trí tuệ nhân tạo (Inference Engine qua **Unity Sentis** / **ONNX**), kiến trúc phân tầng ra quyết định và chấp hành (**Hierarchical Architecture**), mạng nơ-ron đa nhiệm (**Multi-Head Decision & Control Network**), và bộ điều khiển hành vi phản xạ đa tia (**Omnidirectional Perception & Active Evasion**).

---

## Mục lục
- [Giới thiệu tổng quan](#-giới-thiệu-tổng-quan)
- [Kiến trúc Phân tầng 3 Lớp (Hierarchical Architecture)](#-kiến-trúc-phân-tầng-3-lớp-hierarchical-architecture)
- [Tính năng nổi bật](#-tính-năng-nổi-bật)
- [Cấu trúc thư mục & Chức năng các file chính](#-cấu-trúc-thư-mục--chức-năng-các-file-chính)
- [Đặc tả Mô hình Mạng nơ-ron (.onnx)](#-đặc-tả-mô-hình-mạng-nơ-ron-onnx)
- [Yêu cầu hệ thống & Hướng dẫn cài đặt](#-yêu-cầu-hệ-thống--hướng-dẫn-cài-đặt)
- [Hướng dẫn tích hợp Bộ não điều khiển xe vào dự án của bạn](#-hướng-dẫn-tích-hợp-bộ-não-điều-khiển-xe-vào-dự-án-của-bạn)
- [Cấu hình tham số của Bộ não AIFVehicleAgent](#-cấu-hình-tham-số-của-bộ-não-aifvehicleagent)
- [Hướng dẫn sử dụng Hệ thống Người qua đường](#-hướng-dẫn-sử-dụng-hệ-thống-người-qua-đường-pedestrian-spawner)

---

## Giới thiệu tổng quan

Dự án **Traffic Jam Simulation** mô phỏng chân thực các hiện tượng vật lý và tâm lý lái xe trong giao thông đường bộ:
- Hiện tượng **Kẹt xe ma (Phantom Traffic Jams / Stop-and-Go Waves)**: Khi một xe đột ngột đạp phanh nhẹ, sóng dừng phản ứng dây chuyền lan truyền ngược về phía sau đoàn xe tạo thành nút thắt cổ chai dù không có tai nạn hay chướng ngại vật cố định.
- **Tâm lý tài xế phân hóa (`hurryFactor`)**: Mỗi xe có chỉ số "độ vội" khác nhau (từ từ tốn, giữ khoảng cách xa đến vội vã, bứt tốc nhanh, bám sát).
- **Bộ não điều khiển xe toàn diện (`AIFVehicleAgent`)**: Vận hành dựa trên mô hình học máy `.onnx` đa nhiệm (Multi-Head), điều khiển trực tiếp cả **Gia tốc (Acceleration)** và **Góc lái (Steering)**, kết hợp hệ thống cảm biến quang học ảo đa hướng, cơ chế phân tách sườn xe song song và phục hồi sau va chạm.

---

## Kiến trúc Phân tầng 3 Lớp (Hierarchical Architecture)

Bộ não xe được thiết kế theo mô hình phân tầng tiêu chuẩn của hệ thống tự hành tiên tiến:

```
┌────────────────────────────────────────────────────────────────────────┐
│  TẦNG 1: BỘ CHỌN HÀNH VI BẬC CAO (High-Level Decision Head)            │
│  Mạng Nơ-ron AI tự động phân loại và chọn 1 trong 5 hành vi:           │
│  - FreeFlow: Đường thông thoáng, duy trì vận tốc và đi thẳng ổn định   │
│  - CarFollowing: Bám đuôi xe trước theo cự ly và nhịp độ an toàn       │
│  - ActiveEvasion: Chủ động bẻ lái lách né chướng ngại vật/người qua    │
│  - SafeYielding: Giảm tốc / dừng nhường người đi bộ qua đường          │
│  - EmergencyShield: Bảo hiểm phanh khẩn cấp & chống va chạm vật lý     │
└───────────────────────────────────┬────────────────────────────────────┘
                                    │
┌───────────────────────────────────▼────────────────────────────────────┐
│  TẦNG 2: MẠNG NƠ-RON AI THỰC THI CHÍNH SÁCH (Neural Control Policy)    │
│  Input [1, 9] ──► traffic_aif_agent.onnx ──► Output [1, 7]             │
│  • Output[0..4]: Điểm số phân loại hành vi (argmax -> DrivingMode)     │
│  • Output[5]: Gia tốc điều khiển liên tục (Continuous Acceleration)    │
│  • Output[6]: Góc bẻ lái điều khiển liên tục (Continuous Steering)     │
└───────────────────────────────────┬────────────────────────────────────┘
                                    │
┌───────────────────────────────────▼────────────────────────────────────┐
│  TẦNG 3: LỚP BẢO VỆ VẬT LÝ & CHẤP HÀNH (Safety Shield & Actuation)     │
│  - Bộ lọc làm mượt góc lái EMA (Exponential Moving Average)            │
│  - Lực đẩy phân tách sườn (Side Separation Force) khi 2 xe đi sát nhau │
│  - Bảo hiểm mép đường / rào chắn (Curb Collision Guard)                │
│  - Áp dụng vào Rigidbody linearVelocity & rotation                     │
└────────────────────────────────────────────────────────────────────────┘
```

---

## Tính năng nổi bật

1. **AI điều khiển cả Gia tốc và Góc lái (Neural Acceleration & Steering)**:
   - Toàn bộ quyết định tăng tốc, duy trì vận tốc, hãm phanh và bẻ góc lái vi mô được suy luận đồng thời trực tiếp từ mô hình mạng nơ-ron sâu định dạng ONNX chạy qua **Unity Sentis**.
   - Bộ lọc làm mượt góc lái theo thời gian (EMA Filter): Loại bỏ hoàn toàn hiện tượng rung lắc, xoay giật góc bánh xe khi đổi hướng.

2. **Không gian di chuyển liên tục (Continuous Driving Space - Không ép làn cố định)**:
   - Xe không bị ép buộc phải bám cứng vào 3 vạch kẻ đường cố định.
   - Khi cự ly với xe bên cạnh là an toàn ($\ge 0.50\text{m}$), xe tiếp tục đi thẳng ổn định tại vị trí hiện tại.
   - Khi cần né vật cản, xe lách sang khoảng trống an toàn bên cạnh và tiếp tục hành trình tại tọa độ mới đó mà không cần quay lại làn cũ nếu không cần thiết.

3. **Quy tắc điều khiển 2 xe đi song song thông minh**:
   - **Khi 2 xe đi song song nhưng cách nhau an toàn ($\ge 0.50\text{m}$)**: Ưu tiên đi thẳng tuyệt đối, triệt tiêu lạng lách vô cớ.
   - **Khi 2 xe đi song song quá sát nhau ($< 0.50\text{m}$)**: Tầng 3 tự động kích hoạt lực đẩy phân tách sườn (`sideSeparationSteer`) dạt hai xe ra xa nhau để đảm bảo an toàn, sau đó AI tiếp tục giữ quỹ đạo thẳng.

4. **Hệ thống Cảm biến Quang học Đa hướng (Omnidirectional Perception)**:
   - 9 tia cản trước quét vật cản và xe dẫn đầu.
   - 8 tia chéo hình quạt tầm xa đón đầu người đi bộ băng ngang đường từ xa.
   - 4 điểm quét sườn xe mỗi bên quét xe đi song song và khoảng hở sườn.
   - Cảm biến quét trước và sau làn lân cận phục vụ lách né an toàn.
   - Tích hợp `OnDrawGizmosSelected` hiển thị trực quan toàn bộ chùm tia trong Scene view.

5. **Hệ thống Người qua đường & Bộ não né tránh thông minh (Pedestrian Crossing & Evasion)**:
   - **Người đi bộ có cảm biến (`PedestrianAgent`)**: Bắn tia quét $2.5\text{m}$ phía trước. Nếu phát hiện ô tô chắn ngang hoặc đang tiến tới, người đi bộ chủ động dừng bước chờ xe qua.
   - **Xe né người qua đường có điều kiện**: Chỉ chủ động bẻ lái lách né khi phía trước và phía sau không có xe khác ở gần. Nếu có xe khác, xe ưu tiên phanh dừng nhường đường an toàn.
   - **Lách qua trước mặt người qua đường**: Khi khoảng trống trước mặt người qua đường đủ rộng ($\ge 2.5\text{m}$), xe chủ động lách qua trước mặt để lưu thông liên tục.

6. **Tín hiệu đèn hậu & Trực quan hóa va chạm**:
   - **`CarLightsController`**: Điều khiển đèn phanh đỏ rực khi giảm tốc, xi nhan vàng nhấp nháy khi lách né.
   - **`CarCollisionVisualizer`**: Đổi màu toàn bộ thân xe sang đỏ rực khi có va chạm vật lý và tự phục hồi màu sau vài giây.

---

## Cấu trúc thư mục & Chức năng các file chính

```text
TrafficJamSimulation/
├── Assets/
│   ├── Materials/
│   │   ├── CarBodyMaterial.mat         # Vật liệu sơn thân xe (hỗ trợ MaterialPropertyBlock đổi màu)
│   │   ├── PedestrianMaterial.mat      # Vật liệu trang phục người đi bộ qua đường
│   │   ├── RoadMaterial.mat            # Vật liệu nhựa đường cao tốc và rào chắn
│   │   └── TailLightMaterial.mat       # Vật liệu đèn hậu xe (hỗ trợ phát xạ Emission)
│   ├── Models/
│   │   ├── traffic_aif_agent.onnx      # Model ONNX phân tầng đa nhiệm (9 inputs -> 7 outputs)
│   │   └── README.md                   # Hướng dẫn chi tiết Input/Output của mô hình AI
│   ├── Prefabs/
│   │   ├── CarAgent.prefab             # Prefab xe hoàn chỉnh (RigidBody, Agent, Đèn, Visualizer)
│   │   ├── Pedestrian.prefab           # Prefab người đi bộ qua đường (Collider, Agent, Body)
│   │   └── README.md                   # Hướng dẫn tạo Prefab xe tiêu chuẩn
│   ├── Scenes/
│   │   └── TrafficSimulationScene.unity# Scene demo mô phỏng cao tốc 3 làn đường dài 500m
│   └── Scripts/
│       ├── AIFVehicleAgent.cs          # [QUAN TRỌNG] Bộ não phân tầng 3 lớp (Sentis AI + Phản xạ)
│       ├── PedestrianAgent.cs          # Điều khiển người đi bộ qua đường (Raycast né xe, dừng nhường)
│       ├── PedestrianSpawner.cs        # Trình sinh người qua đường tự động hoặc theo vị trí chỉ định
│       ├── VehicleAgent.cs             # Bộ điều khiển xe theo thuật toán cổ điển IDM (Benchmark)
│       ├── TrafficInferenceEngine.cs   # Quản lý nạp và suy luận mô hình ONNX qua Unity Sentis
│       ├── TrafficSpawner.cs           # Script sinh đoàn xe so le đa làn, chống chồng lấn
│       ├── TrafficLaneManager.cs       # Quản lý luồng giao thông trên cung đường
│       ├── TrafficFieldManager.cs      # Singleton theo dõi bản đồ mật độ xe (Mean Field Grid)
│       ├── CarLightsController.cs      # Điều khiển đèn hậu (phanh đỏ, xi nhan vàng nhấp nháy)
│       ├── CarCollisionVisualizer.cs   # Hiệu ứng đổi màu xe đỏ rực khi có va chạm
│       └── Editor/
│           └── EnvironmentSetup.cs     # Công cụ Editor Menu tự động tạo Prefab và Scene 1-click
├── Packages/
│   └── manifest.json                   # Danh sách package (bao gồm com.unity.sentis)
├── ProjectSettings/                    # Cấu hình dự án Unity 6
└── README.md                           # Tài liệu hướng dẫn dự án
```

---

## Đặc tả Mô hình Mạng nơ-ron (.onnx)

Mô hình [`traffic_aif_agent.onnx`](file:///E:/Unity%20Prj/TrafficJamSimulation/Assets/Models/traffic_aif_agent.onnx) vận hành với kiến trúc:

- **Input Tensor `[1, 9]`**:
  1. `normSpeed`: Vận tốc xe ($v / 30.0$).
  2. `normGap`: Cự ly tới vật cản phía trước ($d / 50.0$).
  3. `normRelSpeed`: Vận tốc tương đối với vật cản ($\Delta v / 15.0$).
  4. `speedError`: Sai số vận tốc ($(v_{\text{target}} - v) / v_{\text{target}}$).
  5. `normRoadOffset`: Độ lệch vị trí so với tim đường ($x / \text{halfRoadWidth}$).
  6. `sideBalance`: Cân bằng cự ly hai bên sườn ($(distRight - distLeft) / 5.0$).
  7. `isLeaderBraking`: Đèn phanh xe trước ($1.0$ hoặc $0.0$).
  8. `pedestrianDetected`: Người đi bộ phía trước ($1.0$ hoặc $0.0$).
  9. `trafficDensity`: Mật độ giao thông tại vị trí xe ($[0.0, 1.0]$).

- **Output Tensor `[1, 7]`**:
  - `Output[0..4]`: 5 logits phân loại hành vi Tầng 1 (`FreeFlow`, `CarFollowing`, `ActiveEvasion`, `SafeYielding`, `EmergencyShield`).
  - `Output[5]`: Gia tốc chuẩn hóa $[-1.0, 1.0] \rightarrow a = \text{Output}[5] \times \text{maxAcceleration}$.
  - `Output[6]`: Góc bẻ lái chuẩn hóa $[-1.0, 1.0] \rightarrow \text{steer} = \text{Output}[6] \times 0.45\text{ rad}$.

---

## Yêu cầu hệ thống & Hướng dẫn cài đặt

### Yêu cầu tiên quyết:
- **Unity Version**: Unity **6 (6000.3.x)** trở lên (hoặc Unity 2023.2+ có hỗ trợ Unity Sentis).
- **Unity Sentis Package**: Phiên bản **2.1.3** trở lên (`com.unity.sentis`).
- **Render Pipeline**: Hỗ trợ tốt trên cả **Universal Render Pipeline (URP)** và **Built-in Render Pipeline**.

### Các bước mở và chạy dự án:
1. **Clone repository**:
   ```bash
   git clone https://github.com/FATng/TrafficJamSimulation.git
   ```
2. **Mở dự án trong Unity Hub**:
   - Mở **Unity Hub** $\rightarrow$ chọn **Add** $\rightarrow$ **Add project from disk**.
   - Trỏ tới thư mục `TrafficJamSimulation`.
   - Chọn phiên bản Unity tương ứng (khuyến nghị **Unity 6**).
3. **Mở Scene mô phỏng**:
   - Trong cửa sổ **Project**, mở file: `Assets/Scenes/TrafficSimulationScene.unity`.
4. **Chạy mô phỏng**:
   - Nhấn nút **Play (▶)** trong Unity Editor.
   - Quan sát đoàn xe di chuyển thông minh, tự động phân tầng hành vi, né tránh người đi bộ và tách rời khi đi song song sát nhau.

---

## Hướng dẫn tích hợp Bộ não điều khiển xe vào dự án của bạn

### Bước 1: Cài đặt Unity Sentis
Trong dự án của bạn:
1. Mở menu **Window** $\rightarrow$ **Package Manager**.
2. Nhấn nút **`+`** $\rightarrow$ chọn **Add package by name...** $\rightarrow$ nhập: `com.unity.sentis`.

### Bước 2: Sao chép tệp mã nguồn và Model
Copy các tệp sau từ `Assets/` vào dự án của bạn:
- `Assets/Scripts/AIFVehicleAgent.cs` *(Bộ não phân tầng 3 lớp)*
- `Assets/Scripts/CarLightsController.cs` *(Hệ thống đèn xe)*
- `Assets/Scripts/CarCollisionVisualizer.cs` *(Hiệu ứng va chạm)*
- `Assets/Scripts/TrafficFieldManager.cs` *(Trường mật độ Mean Field)*
- `Assets/Models/traffic_aif_agent.onnx` *(Mô hình ONNX 9 inputs -> 7 outputs)*

### Bước 3: Cấu hình Tag & Rigidbody
1. Đặt Tag của xe là **`Vehicle`**.
2. Cấu hình Rigidbody: `Mass: 1500`, `Collision Detection: Continuous Dynamic`, `Interpolate: Interpolate`.
3. Gắn script `AIFVehicleAgent.cs`, kéo file `traffic_aif_agent.onnx` vào trường `Onnx Model Asset`.

---

## Cấu hình tham số của Bộ não AIFVehicleAgent

```csharp
[Header("Hierarchical Architecture (Kiến trúc phân tầng)")]
[SerializeField] private DrivingMode currentDrivingMode = DrivingMode.FreeFlow;

[Header("Driver Personality (Độ Vội)")]
public float hurryFactor = 1.0f; // 0.75 = Điềm tĩnh; 1.35 = Vội vã, bứt tốc, bám sát

[Header("Road Boundary Limits")]
public float roadWidth = 10f;      // Bề rộng toàn bộ mặt đường (m)
public float maxSafeRoadX = 4.1f;  // Tọa độ mép an toàn cách tim đường

[Header("Heading Angle Constraint")]
public float maxHeadingAngle = 18f;// Giới hạn góc bẻ lái tối đa tránh quay ngược chiều

[Header("Separation & Collision Recovery")]
public float vehicleSeparationSpeed = 1.2f;  // Vận tốc tách nhẹ khi 2 xe chạm nhau
public float curbSeparationSpeed = 2.5f;     // Vận tốc đẩy xe ra khỏi mép rào chắn
public float crashBrakeDecel = 12.0f;        // Gia tốc phanh khẩn cấp khi đâm va
```

---

## Hướng dẫn sử dụng Hệ thống Người qua đường (Pedestrian Spawner)

- **Prefab Người qua đường**: Nằm tại `Assets/Prefabs/Pedestrian.prefab` (Tag: `Pedestrian`).
- **Phím tắt điều khiển kịch bản trong Scene**:
  - Nhấn phím **P**: Sinh 1 người qua đường tại vị trí ngẫu nhiên.
  - Nhấn phím **C**: Sinh người qua đường tại vị trí cố định $Z = 120\text{m}$, đi từ trái sang phải.
  - Nhấn phím **X**: Xóa sạch toàn bộ người qua đường hiện có.

---
