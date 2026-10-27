# 🚗 Traffic Jam Simulation (Unity 6 + Sentis AI)

> **Mô phỏng dòng giao thông thông minh và hiện tượng kẹt xe (Phantom Jam) trên Unity 6**, kết hợp giữa trí tuệ nhân tạo (Inference Engine qua **Unity Sentis** / **ONNX**), mô hình vật lý bám đuôi kinh điển (**Intelligent Driver Model - IDM**), và bộ điều khiển hành vi phản xạ đa tia (**Multi-Raycast Active Perception & Evasion**).

---

## 📌 Mục lục
- [Giới thiệu tổng quan](#-giới-thiệu-tổng-quan)
- [Tính năng nổi bật](#-tính-năng-nổi-bật)
- [Cấu trúc thư mục & Chức năng các file chính](#-cấu-trúc-thư-mục--chức-năng-các-file-chính)
- [Yêu cầu hệ thống & Hướng dẫn cài đặt](#-yêu-cầu-hệ-thống--hướng-dẫn-cài-đặt)
- [Hướng dẫn tích hợp Bộ não điều khiển xe vào dự án của bạn](#-hướng-dẫn-tích-hợp-bộ-não-điều-khiển-xe-vào-dự-án-của-bạn)
- [Cấu hình tham số của Bộ não AIFVehicleAgent](#-cấu-hình-tham-số-của-bộ-não-aifvehicleagent)
- [Giấy phép & Tác giả](#-giấy-phép--tác-giả)

---

## 🌟 Giới thiệu tổng quan

Dự án **Traffic Jam Simulation** mô phỏng chân thực các hiện tượng vật lý và tâm lý lái xe trong giao thông đường bộ:
- Hiện tượng **Kẹt xe ma (Phantom Traffic Jams / Stop-and-Go Waves)**: Khi một xe đột ngột đạp phanh nhẹ, sóng dừng phản ứng dây chuyền lan truyền ngược về phía sau đoàn xe tạo thành nút thắt cổ chai dù không có tai nạn hay chướng ngại vật cố định.
- **Tâm lý tài xế phân hóa (`hurryFactor`)**: Mỗi xe có chỉ số "độ vội" khác nhau (từ từ tốn, giữ khoảng cách xa đến vội vã, bứt tốc nhanh, bám sát).
- **Bộ não điều khiển xe toàn diện (`AIFVehicleAgent`)**: Vận hành dựa trên mô hình học máy `.onnx` kết hợp hệ thống cảm biến quang học ảo (Raycast đa hướng trước và bên sườn), phản xạ theo tín hiệu đèn hậu của xe trước, tự động né xe đi song song và cơ chế phục hồi va chạm (Separation & Recovery).

---

## ✨ Tính năng nổi bật

1. **Bộ não điều khiển xe thông minh (`AIFVehicleAgent`)**:
   - Tích hợp mạng nơ-ron sâu định dạng **ONNX** chạy trực tiếp trong Unity qua **Unity Sentis**.
   - Cảm biến **Multi-Raycast 3 tia phía trước**: Quét phủ rộng toàn bộ thân xe, phát hiện xe trước và đọc trạng thái đèn phanh/xi nhan.
   - Cảm biến **Side Scan 2 bên sườn & chéo góc**: Phát hiện xe chạy song song, chủ động đánh lái sang bên đường trống hoặc tự động điều chỉnh tốc độ lệch pha để tránh kẹp xe.
   - **Khống chế góc quay (Heading Yaw Clamping)**: Khống chế góc quay tối đa trong khoảng $\pm 18^\circ$, ngăn tuyệt đối hiện tượng va chạm làm quay đầu xe đi ngược chiều.
   - **Cố định cao độ (Height Constraint)**: Triệt tiêu biến dạng trục Y, xe không bị nảy hoặc bay lên trời khi va chạm.
   - **Hệ thống phục hồi va chạm (Separation & Recovery)**: Tự động phát hiện va chạm xe - xe hoặc xe - lề đường để tách nhẹ các xe ra và đẩy xe khỏi mép rào chắn.

2. **Mô hình chuẩn mực IDM (`VehicleAgent`)**:
   - Triển khai thuật toán *Intelligent Driver Model (Treiber et al., 2000)* phục vụ mục đích đo lường (benchmark) và đối chiếu với AI.

3. **Quản lý mật độ giao thông trường trung bình (`TrafficFieldManager`)**:
   - Mô hình Mean Field chia đường thành các ô lưới (grid cells), theo dõi mật độ giao thông theo thời gian thực dọc theo trục đường.

4. **Trình sinh xe thông minh (`TrafficSpawner`)**:
   - Tự động bố trí xe trên 3 làn đường theo phương thức so le (staggered).
   - Kiểm tra vật lý OverlapBox trước khi đặt xe, đảm bảo 100% không có xe bị đè hoặc chạm nhau khi xuất phát.

5. **Hệ thống Người qua đường & Phản xạ nhường đường (Pedestrian Crossing & Yielding)**:
   - **`Pedestrian.prefab`**: Nhân vật 3D người đi bộ hoàn chỉnh với `CapsuleCollider`, `Rigidbody`, `PedestrianAgent` và trang phục màu sắc ngẫu nhiên.
   - **`PedestrianSpawner`**: Tự động sinh người qua đường định kỳ hoặc thông qua các hàm `SpawnPedestrian()`, `SpawnPedestrianAt(zPosition, fromLeftToRight, walkingSpeed)`.
   - **Phản xạ nhường đường của xe (`AIFVehicleAgent`)**: Cảm biến đa tia trước phát hiện người đi bộ cắt ngang làn đường từ xa, tự động kích hoạt đèn phanh đỏ rực, giảm tốc dừng hẳn ở cự ly an toàn nhường đường, và tự động tăng tốc trở lại khi người đi bộ đã rời khỏi làn.

6. **Tín hiệu đèn hậu & Trực quan hóa va chạm**:
   - **`CarLightsController`**: Điều khiển đèn phanh (đỏ rực khi đạp phanh), xi nhan vàng nhấp nháy khi chuyển làn/bẻ lái, đèn định vị sau.
   - **`CarCollisionVisualizer`**: Khi 2 xe va chạm hoặc va vào rào chắn, cả 2 xe lập tức đổi sang màu đỏ rực cảnh báo va chạm và tự phục hồi màu sau vài giây.

7. **Thiết lập tự động một chạm (`EnvironmentSetup`)**:
   - Bộ công cụ Unity Editor Menu (`Tools -> Setup Road Environment`, `Tools -> Create CarAgent Prefab`) giúp tái tạo toàn bộ Scene mô phỏng chỉ với một cú nhấp chuột.

---

## 📂 Cấu trúc thư mục & Chức năng các file chính

```text
TrafficJamSimulation/
├── Assets/
│   ├── Materials/
│   │   ├── CarBodyMaterial.mat         # Vật liệu sơn thân xe (hỗ trợ MaterialPropertyBlock đổi màu)
│   │   ├── PedestrianMaterial.mat      # Vật liệu trang phục người đi bộ qua đường
│   │   ├── RoadMaterial.mat            # Vật liệu nhựa đường cao tốc và rào chắn
│   │   └── TailLightMaterial.mat       # Vật liệu đèn hậu xe (hỗ trợ phát xạ Emission)
│   ├── Models/
│   │   ├── traffic_aif_agent.onnx      # Trọng số Neural Network (.onnx) dùng cho Unity Sentis
│   │   └── README.md                   # Hướng dẫn định dạng Input/Output của mô hình AI
│   ├── Prefabs/
│   │   ├── CarAgent.prefab             # Prefab hoàn chỉnh của xe (RigidBody, Agent, Đèn, Visualizer)
│   │   ├── Pedestrian.prefab           # Prefab hoàn chỉnh người đi bộ qua đường (Collider, Agent, Body)
│   │   └── README.md                   # Hướng dẫn tạo Prefab xe tiêu chuẩn
│   ├── Scenes/
│   │   └── TrafficSimulationScene.unity# Scene demo mô phỏng cao tốc 3 làn đường dài 500m
│   └── Scripts/
│       ├── AIFVehicleAgent.cs          # [QUAN TRỌNG] Bộ não điều khiển hành vi xe thông minh (AI + Phản xạ)
│       ├── PedestrianAgent.cs          # Điều khiển hành vi người đi bộ qua đường (bước chân, hướng đi, tự hủy)
│       ├── PedestrianSpawner.cs        # Trình sinh người qua đường tự động hoặc theo vị trí chỉ định
│       ├── VehicleAgent.cs             # Bộ điều khiển xe theo thuật toán cổ điển IDM (Benchmark)
│       ├── TrafficInferenceEngine.cs   # Trình quản lý nạp và suy luận mô hình ONNX qua Sentis
│       ├── TrafficSpawner.cs           # Script sinh đoàn xe đa làn so le, chống chồng lấn
│       ├── TrafficLaneManager.cs       # Quản lý luồng giao thông trên cung đường lặp (Ring) hoặc thẳng
│       ├── TrafficFieldManager.cs      # Singleton theo dõi bản đồ mật độ xe (Mean Field Grid)
│       ├── CarLightsController.cs      # Điều khiển đèn hậu (phanh đỏ, xi nhan vàng nhấp nháy)
│       ├── CarCollisionVisualizer.cs   # Hiệu ứng đổi màu xe đỏ rực khi có va chạm
│       └── Editor/
│           └── EnvironmentSetup.cs     # Công cụ Editor Menu tự động tạo Prefab và Scene 1-click
├── Packages/
│   └── manifest.json                   # Danh sách package (bao gồm com.unity.sentis 2.1.3)
├── ProjectSettings/                    # Cấu hình dự án Unity 6
└── README.md                           # Tài liệu hướng dẫn dự án
```

### Chi tiết các file C# cốt lõi:

| Tên File | Vai trò | Chức năng chi tiết |
| :--- | :--- | :--- |
| **`AIFVehicleAgent.cs`** | **Bộ não AI chính** | Thu thập dữ liệu cảm biến đa tia (khoảng cách xe trước, trạng thái đèn phanh/xi nhan xe trước, xe đi song song bên hông), chạy suy luận ONNX hoặc quy tắc thích ứng, điều khiển ga/phanh, bẻ lái né sườn, giữ góc yaw an toàn, phục hồi va chạm xe-xe và xe-lề. |
| **`VehicleAgent.cs`** | **Bộ não IDM** | Triển khai mô hình vi mô *Intelligent Driver Model* (vận tốc tự do, khoảng cách an toàn động $s^*$, gia tốc cực đại $a$, giảm tốc tiện nghi $b$) và trực quan hóa tốc độ bằng dải màu (Xanh lá $\rightarrow$ Đỏ). |
| **`TrafficInferenceEngine.cs`** | **Cầu nối Sentis** | Nạp file `.onnx` thành `ModelAsset`, khởi tạo `Worker` (CPU hoặc GPUCompute), chuẩn hóa tensor đầu vào `[speed, gap, rel_speed]` và nhận gia tốc đầu ra. |
| **`TrafficSpawner.cs`** | **Tạo sinh lưu lượng** | Rải xe tự động trên 3 làn đường với khoảng cách an toàn biến thiên, so le vị trí Z, gán ngẫu nhiên chỉ số `hurryFactor` (0.75x - 1.35x), kiểm tra OverlapBox vật lý. |
| **`CarLightsController.cs`** | **Hệ thống chiếu sáng** | Tự động kích hoạt đèn phanh đỏ rực khi ga âm hoặc khoảng cách gần, kích hoạt đèn xi nhan vàng nhấp nháy theo tần số khi bẻ lái hoặc chuyển làn ngẫu nhiên. |
| **`CarCollisionVisualizer.cs`** | **Trực quan hóa tai nạn** | Đổi màu toàn bộ thân xe sang màu đỏ cảnh báo tức thì khi xảy ra va chạm với xe khác hoặc rào chắn, đếm ngược thời gian và phục hồi lại màu xe ban đầu. |
| **`TrafficFieldManager.cs`** | **Trường mật độ Mean-Field** | Chia tuyến đường thành lưới 50 ô (cells), các xe liên tục đăng ký vị trí để tính toán mật độ cục bộ và tiêu tán mật độ theo thời gian. |
| **`PedestrianAgent.cs`** | **Người qua đường** | Điều khiển người đi bộ băng ngang lòng đường, xoay mặt theo hướng bước đi (+X/-X), tạo hiệu ứng bước chân nhấp nhô (bobbing), gán bảng màu trang phục ngẫu nhiên và tự giải phóng bộ nhớ khi sang đường xong. |
| **`PedestrianSpawner.cs`** | **Sinh người qua đường** | Tự động sinh người đi bộ định kỳ tại vị trí ngẫu nhiên hoặc các vạch kẻ chỉ định, cung cấp hàm `SpawnPedestrian()` và `SpawnPedestrianAt()` cho phép kích hoạt người qua đường theo kịch bản. |
| **`EnvironmentSetup.cs`** | **Công cụ Editor** | Menu `Tools` trong Unity Editor: Tự động gắn Tag `Vehicle` & `Pedestrian`, tạo vật liệu, tạo `CarAgent.prefab` & `Pedestrian.prefab`, xây dựng đoạn đường cao tốc 500m có rào chắn 2 bên, cấu hình Spawner và căn chỉnh Camera. |

---

## 🛠 Yêu cầu hệ thống & Hướng dẫn cài đặt

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
   - Trong cửa sổ **Project**, điều hướng tới: `Assets/Scenes/TrafficSimulationScene.unity` và nhấn đúp để mở.
4. **Chạy mô phỏng**:
   - Nhấn nút **Play (▶)** trong Unity Editor.
   - Quan sát đoàn xe di chuyển trên 3 làn đường, tự động giữ khoảng cách, né tránh xe chạy song song, phanh đỏ khi gặp chướng ngại và đổi màu khi có va chạm.
5. *(Tùy chọn) Tái tạo lại Scene tự động*:
   - Trên thanh menu Unity, chọn: **`Tools` $\rightarrow$ `Setup Road Environment`**. Hệ thống sẽ tự động cấu hình lại mặt đường, rào chắn, hệ thống spawner và camera.

---

## 🧠 Hướng dẫn tích hợp Bộ não điều khiển xe vào dự án của bạn

Nếu bạn đã có một dự án Unity riêng (Game lái xe, mô phỏng đô thị, hệ thống AI giao thông...) và muốn sử dụng bộ não điều khiển xe thông minh từ dự án này, hãy làm theo các bước sau:

### Bước 1: Cài đặt Unity Sentis
Trong dự án của bạn:
1. Mở menu **Window** $\rightarrow$ **Package Manager**.
2. Nhấn nút dấu **`+`** ở góc trên bên trái $\rightarrow$ chọn **Add package by name...**
3. Nhập:
   ```text
   com.unity.sentis
   ```
4. Nhấn **Add** và chờ Unity tải gói thư viện suy luận AI.

### Bước 2: Sao chép các tệp mã nguồn và Model
Copy các tệp sau từ thư mục `Assets/` của dự án này vào dự án của bạn:
- `Assets/Scripts/AIFVehicleAgent.cs` *(Bộ não AI điều khiển xe)*
- `Assets/Scripts/CarLightsController.cs` *(Hệ thống đèn phanh/xi nhan - nếu dùng)*
- `Assets/Scripts/CarCollisionVisualizer.cs` *(Hiệu ứng đổi màu va chạm - nếu dùng)*
- `Assets/Scripts/TrafficFieldManager.cs` *(Hệ thống mật độ Mean-Field - nếu dùng)*
- `Assets/Models/traffic_aif_agent.onnx` *(Mô hình trí tuệ nhân tạo định dạng ONNX)*

### Bước 3: Cấu hình Tag trong Unity
1. Vào menu **Edit** $\rightarrow$ **Project Settings** $\rightarrow$ **Tags and Layers**.
2. Thêm một Tag mới có tên chính xác là: **`Vehicle`**.
3. *(Lưu ý: Tất cả các xe trong môi trường cần được gán Tag `Vehicle` để cảm biến Raycast nhận diện được nhau).*

### Bước 4: Thiết lập cấu trúc xe (Vehicle GameObject)
Trên GameObject chiếc xe của bạn (hoặc xe 3D model):
1. **Collider**:
   - Thêm một `BoxCollider` bao quanh thân xe.
   - Gán `PhysicsMaterial` có `bounciness = 0` và `friction = 0.05` để xe di chuyển trơn tru, không bị nảy tưng khi va chạm.
2. **Rigidbody**:
   - Thêm component `Rigidbody`.
   - Đặt `Mass`: `1500` (kg).
   - Tắt `Use Gravity`: Bỏ tích (hoặc bật tùy thiết lập, khuyến nghị tắt để kiểm soát độ cao ổn định).
   - Đặt `Collision Detection`: **Continuous Dynamic** (ngăn xe đâm xuyên nhau ở tốc độ cao).
   - Đặt `Interpolate`: **Interpolate** (chuyển động mượt mà, không giật hình).
   - **Constraints**: Tích chọn khóa các trục:
     - `Freeze Position Y` (hoặc cố định qua script `fixedHeightY`).
     - `Freeze Rotation X`
     - `Freeze Rotation Y`
     - `Freeze Rotation Z`
3. **Tag**: Chọn Tag của GameObject là **`Vehicle`**.

### Bước 5: Gắn component `AIFVehicleAgent`
1. Kéo thả script `AIFVehicleAgent.cs` vào GameObject chiếc xe.
2. Tại Inspector của `AIFVehicleAgent`:
   - **`Onnx Model Asset`**: Kéo tệp `traffic_aif_agent.onnx` từ thư mục Project vào trường này.
   - **`Road Width`**: Nhập độ rộng mặt đường thực tế của bạn (mặc định là `10`).
   - **`Max Safe Road X`**: Biên độ tối đa xe được phép đi theo trục X trước khi chạm rào (mặc định `4.1`).
   - **`Fixed Height Y`**: Độ cao cố định của tâm xe tính từ mặt đường (ví dụ `0.5`).
   - **`Target Speed`**: Tốc độ hành trình mong muốn (m/s, mặc định `15` ~ 54 km/h).
   - **`Max Heading Angle`**: Góc lệch lái tối đa (khuyến nghị `15` đến `18` độ).
3. *(Tùy chọn gắn đèn xe)*:
   - Nếu xe có 2 MeshRenderer đèn sau, thêm `CarLightsController`, gán 2 đèn vào `Left Tail Light Renderer` và `Right Tail Light Renderer`.
   - Gán `CarLightsController` vào trường `Lights Controller` trên `AIFVehicleAgent`.
4. *(Tùy chọn gắn hiệu ứng va chạm)*:
   - Thêm `CarCollisionVisualizer`, gán `Body Renderer` là MeshRenderer thân xe.
   - Gán vào trường `Collision Visualizer` trên `AIFVehicleAgent`.

---

## ⚙️ Cấu hình tham số của Bộ não AIFVehicleAgent

Dưới đây là bảng giải thích chi tiết các thông số trong `AIFVehicleAgent` để bạn tinh chỉnh theo từng kịch bản:

```csharp
[Header("Driver Personality (Độ Vội)")]
public float hurryFactor = 1.0f; // 0.75 = Điềm tĩnh, nhường đường; 1.35 = Vội vã, bứt tốc, bám sát

[Header("Road Boundary Limits")]
public float roadWidth = 10f;      // Bề rộng toàn bộ mặt đường (m)
public float maxSafeRoadX = 4.1f;  // Tọa độ mép an toàn cách tim đường

[Header("Heading Angle Constraint")]
public float maxHeadingAngle = 18f;// Giới hạn góc bẻ lái tối đa tránh quay ngược chiều

[Header("Side Vehicle Detection")]
public float sideScanDistance = 2.8f;        // Khoảng cách quét phát hiện xe chạy song song (m)
public float sideEvasionSteerStrength = 0.40f;// Lực đánh lái né sang bên đường trống

[Header("Separation & Collision Recovery")]
public float vehicleSeparationSpeed = 1.2f;  // Vận tốc tách nhẹ khi 2 xe chạm nhau
public float curbSeparationSpeed = 2.5f;     // Vận tốc đẩy xe ra khỏi mép rào chắn
public float crashBrakeDecel = 12.0f;        // Gia tốc phanh khẩn cấp khi đâm va
```

---

## 📊 Mô hình Mạng nơ-ron (.onnx) Input & Output

Mô hình AI điều khiển tăng/giảm tốc được nạp thông qua Unity Sentis có đặc tả tensor như sau:

- **Input Tensor `[1, 3]`**:
  1. `speed`: Vận tốc hiện tại của xe (chuẩn hóa chia cho $30.0\text{ m/s}$).
  2. `distanceToLeader`: Khoảng cách tới xe liền trước (chuẩn hóa chia cho $50.0\text{ m}$).
  3. `relativeVelocity`: Vận tốc tương đối ($v_{leader} - v_{current}$, chuẩn hóa chia cho $15.0\text{ m/s}$).
- **Output Tensor `[1, 1]`**:
  - `acceleration`: Giá trị gia tốc dự đoán $a \in [-6.0, +3.0]\text{ m/s}^2$.

---

---

## 🚶‍♂️ Hướng dẫn sử dụng Hệ thống Người qua đường (Pedestrian Spawner)

### 1. Prefab Người qua đường (`Pedestrian.prefab`):
- Nằm tại: `Assets/Prefabs/Pedestrian.prefab` (Tag: `Pedestrian`).
- Được gắn script [`PedestrianAgent.cs`](file:///E:/Unity%20Prj/TrafficJamSimulation/Assets/Scripts/PedestrianAgent.cs), tự động bước qua đường với tốc độ cấu hình (mặc định $1.4	ext{ m/s}$), có nhịp bước chân nhấp nhô sinh động và tự động hủy sau khi bước lên lề bên kia.

### 2. Các hàm sinh người qua đường bằng mã C#:
Bạn có thể tham chiếu tới `PedestrianSpawner` trong Scene để gọi sinh người qua đường bất cứ lúc nào:

```csharp
using TrafficJamSimulation;
using UnityEngine;

public class TrafficScenarioController : MonoBehaviour
{
    [SerializeField] private PedestrianSpawner pedestrianSpawner;

    void Update()
    {
        // Nhấn phím P: Sinh 1 người qua đường tại vị trí ngẫu nhiên
        if (Input.GetKeyDown(KeyCode.P))
        {
            GameObject ped = pedestrianSpawner.SpawnPedestrian();
            Debug.Log($"Đã sinh người qua đường tại: {ped.transform.position}");
        }

        // Nhấn phím C: Sinh người qua đường tại vị trí cố định Z = 120m, đi từ trái sang phải với tốc độ 1.5 m/s
        if (Input.GetKeyDown(KeyCode.C))
        {
            pedestrianSpawner.SpawnPedestrianAt(
                zPosition: 120f,
                fromLeftToRight: true,
                walkingSpeed: 1.5f
            );
        }

        // Nhấn phím X: Xóa sạch toàn bộ người qua đường hiện có
        if (Input.GetKeyDown(KeyCode.X))
        {
            pedestrianSpawner.ClearAllPedestrians();
        }
    }
}
```

---

## 📄 Giấy phép & Tác giả

- **Tác giả**: [FATng](https://github.com/FATng)
- **Repository**: [https://github.com/FATng/TrafficJamSimulation](https://github.com/FATng/TrafficJamSimulation)
- **Mục đích**: Nghiên cứu mô phỏng hành vi giao thông, hiện tượng kẹt xe sóng dừng (phantom traffic jams) và ứng dụng học máy / Unity Sentis trong điều khiển tự hành thời gian thực.
