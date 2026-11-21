# Mô phỏng Dòng Giao thông Thông minh (Traffic Jam Simulation)

Mô phỏng dòng giao thông thông minh và hiện tượng kẹt xe trên nền tảng Unity 6, kết hợp giữa trí tuệ nhân tạo (artificial intelligence - AI) thực thi suy luận (inference) thời gian thực qua Unity Sentis và mô hình định dạng ONNX, kiến trúc phân tầng (hierarchical) ra quyết định và chấp hành, mạng nơ-ron đa nhiệm (multi-task neural network), cùng hệ thống cảm biến quang học đa hướng và cơ chế phòng vệ vật lý.

---

## Mục lục
- [Tổng quan dự án](#tổng-quan-dự-án)
- [Kiến trúc mô hình trí tuệ nhân tạo](#kiến-trúc-mô-hình-trí-tuệ-nhân-tạo)
  - [Cấu trúc mạng nơ-ron đa nhiệm](#cấu-trúc-mạng-nơ-ron-đa-nhiệm)
  - [Đặc tả tensor đầu vào](#đặc-tả-tensor-đầu-vào)
  - [Đặc tả tensor đầu ra](#đặc-tả-tensor-đầu-ra)
- [Quy trình huấn luyện mô hình](#quy-trình-huấn-luyện-mô-hình)
  - [Phương pháp sinh dữ liệu tổng hợp](#phương-pháp-sinh-dữ-liệu-tổng-hợp)
  - [Quy tắc gán nhãn quyết định hành vi](#quy-tắc-gán-nhãn-quyết-định-hành-vi)
  - [Xác định mục tiêu điều khiển liên tục](#xác-định-mục-tiêu-điều-khiển-liên-tục)
  - [Hàm mất mát và tối ưu hóa](#hàm-mất-mát-và-tối-ưu-hóa)
  - [Xuất mô hình sang định dạng ONNX](#xuất-mô-hình-sang-định-dạng-onnx)
  - [Các bước thực hiện huấn luyện](#các-bước-thực-hiện-huấn-luyện)
- [Suy luận thời gian thực trên Unity Sentis](#suy-luận-thời-gian-thực-trên-unity-sentis)
  - [Cơ chế nạp và thực thi mô hình](#cơ-chế-nạp-và-thực-thi-mô-hình)
  - [Giải mã kết quả và áp dụng vật lý](#giải-mã-kết-quả-và-áp-dụng-vật-lý)
- [Cấu trúc thư mục dự án](#cấu-trúc-thư-mục-dự-án)
- [Hướng dẫn cài đặt và chạy mô phỏng](#hướng-dẫn-cài-đặt-và-chạy-mô-phỏng)
  - [Yêu cầu hệ thống](#yêu-cầu-hệ-thống)
  - [Các bước khởi chạy](#các-bước-khởi-chạy)
  - [Phím tắt tương tác](#phím-tắt-tương-tác)
- [Cấu hình tham số](#cấu-hình-tham-số)
- [Video thử nghiệm](#video-thử-nghiệm)

---

## Tổng quan dự án

Dự án tập trung giải quyết bài toán mô phỏng dòng xe tự hành trong đô thị và cao tốc với các hiện tượng giao thông phức tạp:
- Hiện tượng kẹt xe lan truyền ngược: Khi một phương tiện giảm tốc đột ngột, sóng dừng phản ứng dây chuyền lan truyền về phía sau đoàn xe, tạo thành điểm ùn tắc cục bộ dù không có tai nạn hay chướng ngại vật cố định.
- Phân hóa cá tính người lái: Chỉ số độ vội vàng của tài xế tác động trực tiếp đến tốc độ mong muốn và khoảng cách bám đuôi an toàn.
- Điều khiển đa biến số thời gian thực: Không chỉ tính toán gia tốc dọc, mô hình AI trực tiếp điều khiển cả góc đánh lái vi mô, cho phép xe di chuyển trong không gian liên tục mà không bị gò bó vào làn cố định.

---

## Kiến trúc mô hình trí tuệ nhân tạo

### Cấu trúc mạng nơ-ron đa nhiệm

Mô hình được triển khai trong tệp `train_decision_agent.py` dưới tên lớp `TrafficHierarchicalPolicy`, kế thừa từ module mạng nơ-ron của PyTorch. Mô hình áp dụng kiến trúc chia sẻ đặc trưng kết hợp hai nhánh đầu ra chuyên biệt:

```
Đầu vào Tensor [batch_size, 9]
                  │
                  ▼
┌──────────────────────────────────────────────┐
│  Lớp trích xuất đặc trưng dùng chung         │
│  - Linear(9, 64) -> ReLU                     │
│  - Linear(64, 64) -> ReLU                    │
└──────────────────────┬───────────────────────┘
                       │
         ┌─────────────┴─────────────┐
         ▼                           ▼
┌─────────────────────────┐ ┌─────────────────────────┐
│ Nhánh phân loại hành vi │ │ Nhánh điều khiển        │
│ (Decision Head)         │ │ (Control Head)          │
│ - Linear(64, 5)         │ │ - Linear(64, 2)         │
│                         │ │ - Tanh()                │
└────────────┬────────────┘ └────────────┬────────────┘
             │                           │
             └─────────────┬─────────────┘
                           ▼
              Đầu ra Tensor [batch_size, 7]
              - Chỉ số [0..4]: 5 điểm số hành vi
              - Chỉ số [5]: Gia tốc [-1.0, 1.0]
              - Chỉ số [6]: Góc lái [-1.0, 1.0]
```

1. Lớp trích xuất đặc trưng dùng chung:
   - Gồm 2 lớp tuyến tính liên kết đầy đủ, mỗi lớp chứa 64 nơ-ron.
   - Sử dụng hàm kích hoạt chỉnh lưu tuyến tính (rectified linear unit - ReLU) nhằm mô hình hóa các mối quan hệ phi tuyến phức tạp giữa vị trí, tốc độ và môi trường xung quanh.
   - Các đặc trưng sau khi trích xuất được chuyển tiếp đồng thời đến cả hai nhánh ra quyết định.

2. Nhánh phân loại hành vi bậc cao:
   - Gồm 1 lớp tuyến tính biến đổi từ 64 đặc trưng sang 5 giá trị điểm số chưa chuẩn hóa (logits).
   - Đại diện cho 5 chế độ lái xe:
     - 0 (FreeFlow): Đường thông thoáng, duy trì tốc độ tối ưu và hành trình ổn định.
     - 1 (CarFollowing): Bám đuôi xe phía trước theo nhịp độ an toàn.
     - 2 (ActiveEvasion): Chủ động bẻ lái vượt qua chướng ngại vật hoặc người qua đường.
     - 3 (SafeYielding): Giảm tốc chủ động và dừng nhường người đi bộ.
     - 4 (EmergencyShield): Phanh khẩn cấp để ngăn chặn nguy cơ va chạm trực diện.

3. Nhánh điều khiển liên tục:
   - Gồm 1 lớp tuyến tính chuyển đổi từ 64 đặc trưng sang 2 giá trị điều khiển, đi qua hàm kích hoạt tang hyperbolic (hyperbolic tangent - Tanh).
   - Giá trị đầu ra nằm chặt chẽ trong đoạn từ -1.0 đến 1.0, đại diện cho gia tốc chuẩn hóa và góc đánh lái chuẩn hóa.

4. Ghép tensor đầu ra:
   - Hai nhánh được ghép nối theo chiều cuối cùng tạo thành một tensor duy nhất kích thước `[batch_size, 7]`.
   - Thiết kế này giúp bộ máy suy luận Sentis trong Unity thực thi toàn bộ quy trình ra quyết định và điều khiển chỉ trong một lượt tính toán thuận duy nhất.

---

### Đặc tả tensor đầu vào

Đầu vào của mạng là một vector gồm 9 đặc trưng đã được chuẩn hóa (normalization):

| Thứ tự | Tên biến số | Khoảng giá trị | Công thức chuẩn hóa | Ý nghĩa vật lý |
| :---: | :--- | :---: | :--- | :--- |
| 0 | normSpeed | [0.0, 1.0] | v / 30.0 | Tốc độ hiện tại của xe so với vận tốc tối đa tham chiếu (30 m/s) |
| 1 | normGap | [0.0, 1.0] | d / 50.0 | Cự ly tới xe hoặc vật cản gần nhất phía trước (tối đa 50 m) |
| 2 | normRelSpeed | [-1.0, 1.0] | (v_truoc - v) / 15.0 | Tốc độ tương đối giữa xe phía trước và xe hiện tại |
| 3 | speedError | [-1.0, 1.0] | (v_muc_tieu - v) / v_muc_tieu | Sai lệch giữa tốc độ mong muốn và tốc độ thực tế |
| 4 | roadOffset | [-1.0, 1.0] | x / nua_chieu_rong_duong | Độ lệch vị trí ngang của xe so với tim đường |
| 5 | sideBalance | [-1.0, 1.0] | (khoang_trong_phai - khoang_trong_trai) / 5.0 | Mức độ cân bằng không gian giữa hai bên sườn xe |
| 6 | leaderBraking | {0.0, 1.0} | 1.0 nếu xe trước đang phanh, ngược lại 0.0 | Trạng thái đèn báo phanh của xe dẫn đầu |
| 7 | pedestrian | {0.0, 1.0} | 1.0 nếu phát hiện người qua đường, ngược lại 0.0 | Tín hiệu phát hiện người đi bộ trong vùng nguy hiểm |
| 8 | density | [0.0, 1.0] | densityMap[cellIndex] / 10.0 | Mật độ phương tiện cục bộ trên đoạn đường hiện tại |

---

### Đặc tả tensor đầu ra

Đầu ra của mạng gồm 7 phần tử:

| Chỉ số | Đại lượng | Khoảng giá trị | Xử lý tại bộ điều khiển C# trong Unity |
| :---: | :--- | :---: | :--- |
| 0 | Điểm FreeFlow | Số thực | Tìm vị trí có điểm số lớn nhất (argmax) trong 5 giá trị từ 0 đến 4 để xác định chế độ lái xe |
| 1 | Điểm CarFollowing | Số thực | Nếu chỉ số là 1: Xe duy trì khoảng cách bám đuôi |
| 2 | Điểm ActiveEvasion | Số thực | Nếu chỉ số là 2: Kích hoạt đèn xi nhan và chuyển sang quỹ đạo né |
| 3 | Điểm SafeYielding | Số thực | Nếu chỉ số là 3: Bật đèn phanh, dừng trước vạch người đi bộ |
| 4 | Điểm EmergencyShield | Số thực | Nếu chỉ số là 4: Kích hoạt phanh khẩn cấp và báo hiệu va chạm |
| 5 | Gia tốc chuẩn hóa | [-1.0, 1.0] | Nhân với gia tốc tối đa: `accel = output[5] * maxAcceleration` |
| 6 | Góc lái chuẩn hóa | [-1.0, 1.0] | Nhân với góc lái tối đa: `steer = output[6] * 0.45` |

---

## Quy trình huấn luyện mô hình

### Phương pháp sinh dữ liệu tổng hợp

Tập dữ liệu huấn luyện được sinh tự động hoàn toàn bằng phương pháp mô phỏng tham số hóa với 120.000 tình huống giao thông đa biến. Mỗi mẫu dữ liệu đại diện cho một trạng thái quan sát tức thời của phương tiện trên đường:

- Vận tốc xe: Phân bố ngẫu nhiên đều trong khoảng từ 0.0 đến 1.0.
- Cự ly phía trước: Phân bố ngẫu nhiên đều từ 0.02 đến 1.0.
- Vận tốc tương đối: Phân bố ngẫu nhiên từ -1.0 đến 1.0.
- Sai lệch vận tốc: Phân bố ngẫu nhiên từ -1.0 đến 1.0.
- Độ lệch tim đường: Phân bố ngẫu nhiên từ -1.0 đến 1.0.
- Cân bằng sườn xe: Phân bố ngẫu nhiên từ -1.0 đến 1.0.
- Xe trước phanh: Phân bố Bernoulli với xác suất phanh là 25%.
- Người đi bộ xuất hiện: Phân bố Bernoulli với xác suất 15%.
- Mật độ giao thông: Phân bố ngẫu nhiên đều từ 0.0 đến 1.0.

Các mảng dữ liệu thành phần được ghép thành ma trận đầu vào `X` có kích thước `[120000, 9]` kiểu số thực 32-bit.

---

### Quy tắc gán nhãn quyết định hành vi

Hệ thống áp dụng logic phân cấp quy tắc chuyên gia để xác định nhãn chế độ lái xe chính xác cho từng mẫu:

1. Trường hợp phanh khẩn cấp (EmergencyShield - Nhãn 4):
   - Điều kiện: Cự ly phía trước nhỏ hơn 0.08 (tương đương dưới 4 mét), hoặc cự ly dưới 0.15 đồng thời vận tốc thu hẹp nhanh hơn -0.4.
   - Mức độ ưu tiên: Cao nhất.

2. Trường hợp dừng nhường người đi bộ (SafeYielding - Nhãn 3):
   - Điều kiện: Không rơi vào trường hợp khẩn cấp, có người đi bộ xuất hiện phía trước và cự ly nhỏ hơn 0.25 (dưới 12.5 mét).
   - Mức độ ưu tiên: Ưu tiên thứ hai.

3. Trường hợp chủ động lách né (ActiveEvasion - Nhãn 2):
   - Điều kiện: Không thuộc hai trường hợp trên, cự ly phía trước nhỏ hơn 0.35 và sườn xe có khoảng trống rõ rệt (độ lệch cân bằng sườn lớn hơn 0.20).
   - Mức độ ưu tiên: Ưu tiên thứ ba.

4. Trường hợp bám đuôi thông thường (CarFollowing - Nhãn 1):
   - Điều kiện: Không thuộc các trường hợp trên và cự ly tới xe trước nhỏ hơn 0.45.
   - Mức độ ưu tiên: Ưu tiên thứ tư.

5. Trường hợp chạy tự do (FreeFlow - Nhãn 0):
   - Điều kiện: Toàn bộ các điều kiện trên đều không thỏa mãn, đường phía trước hoàn toàn thông thoáng.

---

### Xác định mục tiêu điều khiển liên tục

Mỗi mẫu dữ liệu được tính toán cặp giá trị mục tiêu gồm gia tốc và góc đánh lái dựa trên chế độ lái đã được xác định:

1. Góc đánh lái cơ sở:
   - Được khởi tạo tỷ lệ nghịch với độ lệch tâm đường để xe luôn có xu hướng quay về vùng an toàn: `target_steer = -0.35 * road_offset`.

2. Mục tiêu cho chế độ FreeFlow:
   - Gia tốc điều chỉnh theo sai lệch vận tốc: `target_accel = clip(0.8 * speed_error, -0.2, 1.0)`.
   - Góc lái giữ nguyên góc cơ sở để đi thẳng ổn định.

3. Mục tiêu cho chế độ CarFollowing:
   - Gia tốc phụ thuộc vào vận tốc tương đối và tín hiệu phanh của xe dẫn đầu: `target_accel = clip(0.6 * rel_speed - 0.3 * leader_braking, -0.8, 0.5)`.

4. Mục tiêu cho chế độ ActiveEvasion:
   - Hướng lách được chọn về phía bên sườn có khoảng trống rộng hơn: `evade_dir = sign(side_balance)`.
   - Gia tốc giảm nhẹ để đảm bảo an toàn khi đổi hướng: `target_accel = -0.15`.
   - Góc đánh lái cộng thêm thành phần bẻ lái chủ động: `target_steer = clip(target_steer + 0.45 * evade_dir, -1.0, 1.0)`.

5. Mục tiêu cho chế độ SafeYielding:
   - Phương tiện phanh mạnh chủ động để dừng trước người đi bộ: `target_accel = -0.95`.
   - Góc đánh lái triệt tiêu về 0.0 để tránh mất lái khi dừng.

6. Mục tiêu cho chế độ EmergencyShield:
   - Phương tiện đạp phanh tối đa: `target_accel = -1.0`.
   - Góc đánh lái khóa về 0.0.

Cặp giá trị gia tốc và góc lái được ghép thành ma trận mục tiêu `Y_control` có kích thước `[120000, 2]`.

---

### Hàm mất mát và tối ưu hóa

Quá trình huấn luyện tối ưu hóa đồng thời cả hai mục tiêu thông qua hàm mất mát (loss function) kết hợp:

$$\text{Loss} = \text{Loss}_{\text{classification}} + 2.0 \times \text{Loss}_{\text{regression}}$$

Trong đó:
- $\text{Loss}_{\text{classification}}$: Tính bằng CrossEntropyLoss giữa 5 giá trị logit của mạng và nhãn hành vi thực tế.
- $\text{Loss}_{\text{regression}}$: Tính bằng sai số bình phương trung bình (mean squared error - MSE) giữa 2 giá trị điều khiển dự đoán và bộ giá trị điều khiển mục tiêu.
- Hệ số 2.0 dành trọng số cao hơn cho độ chính xác của gia tốc và góc lái, giúp xe di chuyển mượt mà và giảm giật.

Thuật toán tối ưu:
- Sử dụng Adam Optimizer với tốc độ học (learning rate) bằng 0.003.
- Kích thước lô (batch size): 256 mẫu mỗi lần cập nhật trọng số.
- Số lượt huấn luyện (epochs): 12 lượt trên toàn bộ 120.000 mẫu dữ liệu.
- Độ chính xác phân loại sau khi huấn luyện đạt trên 98%.

---

### Xuất mô hình sang định dạng ONNX

Sau khi quá trình huấn luyện hoàn tất, mô hình được đưa về chế độ đánh giá và xuất trực tiếp sang tệp định dạng ONNX thông qua hàm `torch.onnx.export`:
- Đầu vào giả định: Tensor kiểu số thực 32-bit kích thước `[1, 9]`.
- Phiên bản toán tử (opset version): 13, đảm bảo tương thích tối đa với bộ nhân tính toán của Unity Sentis.
- Bật tính năng gập hằng số (constant folding) để tối ưu cấu trúc đồ thị tính toán và tăng tốc độ suy luận.
- Trục động (dynamic axes): Thiết lập trục lô có thể thay đổi linh hoạt (`batch_size`), cho phép Unity suy luận cho 1 xe đơn lẻ hoặc một lô gồm hàng chục xe cùng lúc.
- Đường dẫn xuất tự động lưu đè lên tệp: `Assets/Models/traffic_aif_agent.onnx`.

---

### Các bước thực hiện huấn luyện

#### Bước 1: Chuẩn bị môi trường Python
Yêu cầu Python phiên bản 3.9 trở lên đã cài đặt các thư viện sau:
```bash
pip install torch numpy onnx
```

#### Bước 2: Chạy tập lệnh huấn luyện
Thực thi lệnh sau tại thư mục gốc của dự án:
```bash
python train_decision_agent.py
```
Hoặc trên Windows PowerShell:
```powershell
python train_decision_agent.py
```

#### Bước 3: Kiểm tra kết quả
- Tập lệnh hiển thị thông số mất mát và độ chính xác qua từng lượt huấn luyện trên màn hình điều khiển.
- Khi kết thúc, tệp `Assets/Models/traffic_aif_agent.onnx` được tạo mới.
- Unity Editor tự động nhận diện và biên dịch mô hình sang tài nguyên của Sentis mà không cần thao tác thủ công.

---

## Suy luận thời gian thực trên Unity Sentis

### Cơ chế nạp và thực thi mô hình

Quá trình suy luận trên mỗi chiếc xe được quản lý bởi tệp `AIFVehicleAgent.cs` thông qua các thành phần của Unity Sentis:

1. Khởi tạo trình thực thi (Worker):
   - Tại thời điểm khởi chạy, tài nguyên `ModelAsset` được biên dịch thành mô hình thời gian chạy bằng `ModelLoader.Load(onnxModelAsset)`.
   - Trình thực thi được tạo với chế độ tính toán đa luồng trên bộ vi xử lý trung tâm (CPU) hoặc bộ xử lý đồ họa (GPU Compute):
   ```csharp
   _runtimeModel = ModelLoader.Load(onnxModelAsset);
   _worker = new Worker(_runtimeModel, BackendType.CPU);
   ```

2. Thu thập và chuẩn hóa dữ liệu cảm biến:
   - Mỗi chu kỳ cập nhật vật lý (FixedUpdate), xe kích hoạt hệ thống cảm biến quang học bắn tia (raycast):
     - 9 tia phía trước xác định cự ly và tốc độ tương đối tới vật cản.
     - 8 tia chéo quạt hình nan quạt tầm xa phát hiện người qua đường từ khoảng cách 20 mét.
     - 8 tia quét ngang hai bên sườn đo khoảng trống tiếp giáp.
     - Cung cấp thông số độ lệch tim đường và mật độ cục bộ lấy từ `TrafficFieldManager`.
   - Dữ liệu được đóng gói vào mảng số thực 9 phần tử và nạp vào tensor đầu vào có hình dạng `[1, 9]`.

3. Thực thi suy luận:
   - Trình thực thi lên lịch tính toán:
   ```csharp
   using var inputTensor = new Tensor<float>(new TensorShape(1, 9), rawInputs);
   _worker.Schedule(inputTensor);
   var outputTensor = _worker.PeekOutput() as Tensor<float>;
   float[] results = outputTensor.DownloadToArray();
   ```

---

### Giải mã kết quả và áp dụng vật lý

Kết quả suy luận trả về được giải mã qua hai bước phân tầng:

1. Giải mã quyết định bậc cao:
   - Duyệt qua 5 phần tử đầu tiên của mảng kết quả từ `results[0]` đến `results[4]`.
   - Phần tử có điểm số cao nhất xác định chế độ lái xe hiện tại.
   - Chế độ lái xe điều khiển hệ thống đèn báo hiệu trên xe (đèn phanh, đèn xi nhan) và thiết lập trạng thái phản xạ.

2. Quy đổi giá trị điều khiển liên tục:
   - Gia tốc tức thời: `accel = results[5] * maxAcceleration`.
   - Góc lái tức thời: `steer = results[6] * 0.45f` (đơn vị radian).

3. Tầng bảo hiểm vật lý và làm mượt:
   - Bộ lọc trung bình động lũy thừa (exponential moving average - EMA) làm mượt góc đánh lái qua thời gian nhằm triệt tiêu hiện tượng rung lắc tay lái.
   - Lực phân tách sườn: Khi hai xe chạy song song ở cự ly quá gần (dưới 50 cm), lực đẩy ngang tự động tách hai xe ra xa để bảo vệ thân xe.
   - Bảo vệ mép đường: Khi xe tiến quá gần rào chắn hoặc lề đường, lực cản hành trình tự động giữ xe trong phạm vi lòng đường cho phép.
   - Giá trị lực và vận tốc cuối cùng được nạp trực tiếp vào thành phần Rigidbody của xe.

---

## Cấu trúc thư mục dự án

```text
TrafficJamSimulation/
├── Assets/
│   ├── Materials/                      # Vật liệu thân xe, đèn tín hiệu, mặt đường
│   ├── Models/
│   │   ├── traffic_aif_agent.onnx      # Mô hình mạng nơ-ron xuất từ PyTorch (9 đầu vào, 7 đầu ra)
│   │   └── README.md                   # Đặc tả kỹ thuật mô hình tại thư mục Models
│   ├── Prefabs/
│   │   ├── CarAgent.prefab             # Mẫu phương tiện lắp ráp hoàn chỉnh
│   │   ├── Pedestrian.prefab           # Mẫu người đi bộ qua đường
│   │   └── README.md                   # Hướng dẫn thiết lập mẫu xe
│   ├── Scenes/
│   │   └── TrafficSimulationScene.unity# Cảnh mô phỏng cao tốc 3 làn đường dài 500 mét
│   └── Scripts/
│       ├── AIFVehicleAgent.cs          # Bộ điều khiển xe dựa trên Sentis AI và phản xạ vật lý
│       ├── CarCollisionVisualizer.cs   # Hiệu ứng đổi màu xe sang đỏ rực khi va chạm
│       ├── CarLightsController.cs      # Bộ điều khiển đèn phanh và đèn xi nhan
│       ├── PedestrianAgent.cs          # Điều khiển người đi bộ qua đường và phản xạ dừng chờ
│       ├── PedestrianSpawner.cs        # Trình sinh người đi bộ tự động hoặc theo vị trí
│       ├── TrafficFieldManager.cs      # Quản lý bản đồ mật độ giao thông toàn cục
│       ├── TrafficInferenceEngine.cs   # Trình quản lý suy luận mô hình ONNX dùng chung
│       ├── TrafficLaneManager.cs       # Quản lý luồng xe và thu thập chỉ số ùn tắc
│       ├── TrafficSpawner.cs           # Script sinh đoàn xe so le chống đè lẫn nhau
│       ├── VehicleAgent.cs             # Bộ điều khiển xe theo mô hình IDM cổ điển làm chuẩn so sánh
│       └── Editor/
│           └── EnvironmentSetup.cs     # Công cụ Editor tự động khởi tạo Scene và vật liệu
├── train_decision_agent.py             # Tập lệnh PyTorch huấn luyện và xuất mô hình ONNX
├── Packages/
│   └── manifest.json                   # Danh sách gói phụ thuộc gồm com.unity.sentis
├── ProjectSettings/                    # Cấu hình thông số dự án Unity 6
└── README.md                           # Tài liệu hướng dẫn tổng thể dự án
```

---

## Hướng dẫn cài đặt và chạy mô phỏng

### Yêu cầu hệ thống
- Phiên bản Unity: Unity 6 (phiên bản 6000.0 trở lên) hoặc Unity 2023.2 trở lên có hỗ trợ Unity Sentis.
- Gói Unity Sentis: Phiên bản 2.1.3 trở lên (`com.unity.sentis`).
- Môi trường Python (nếu muốn huấn luyện lại): Python 3.9 trở lên với PyTorch, NumPy và ONNX.

### Các bước khởi chạy
1. Sao chép kho mã nguồn về máy:
   ```bash
   git clone https://github.com/FATng/TrafficJamSimulation.git
   ```
2. Khởi động Unity Hub, chọn Open và trỏ tới thư mục `TrafficJamSimulation`.
3. Mở cảnh mô phỏng: Vào thư mục `Assets/Scenes/`, mở tệp `TrafficSimulationScene.unity`.
4. Nhấn nút Play trên thanh công cụ của Unity để bắt đầu chạy mô phỏng.

### Phím tắt tương tác
Trong quá trình mô phỏng đang chạy, có thể sử dụng các phím sau trong cửa sổ Game view:
- Phím P: Sinh ngẫu nhiên một người đi bộ băng qua đường.
- Phím C: Sinh người đi bộ tại vị trí cố định trên trục dọc (Z = 120 mét).
- Phím X: Xóa toàn bộ người đi bộ đang có trên đường.

---

## Cấu hình tham số

Các tham số chính trên thành phần `AIFVehicleAgent` có thể điều chỉnh trong Inspector:

| Tham số | Kiểu dữ liệu | Giá trị mặc định | Mô tả chức năng |
| :--- | :---: | :---: | :--- |
| onnxModelAsset | ModelAsset | traffic_aif_agent | Tệp mô hình mạng nơ-ron định dạng ONNX |
| hurryFactor | float | 1.0 | Hệ số vội vàng của tài xế (0.75: cẩn thận; 1.35: vội vã, bám sát) |
| targetSpeed | float | 14.0 | Vận tốc hành trình mong muốn (m/s) |
| maxAcceleration | float | 3.0 | Gia tốc tăng tốc tối đa (m/s^2) |
| roadWidth | float | 10.0 | Bề rộng toàn bộ lòng đường (m) |
| maxSafeRoadX | float | 4.1 | Giới hạn vị trí lề đường an toàn tính từ tim đường (m) |
| maxHeadingAngle | float | 18.0 | Giới hạn góc đánh lái tối đa tránh quay ngang xe (độ) |
| vehicleSeparationSpeed | float | 1.2 | Vận tốc đẩy ngang tách nhau khi hai xe chạy sát (m/s) |
| crashBrakeDecel | float | 12.0 | Gia tốc phanh khẩn cấp khi xảy ra va chạm (m/s^2) |

---

## Video thử nghiệm

Liên kết xem video ghi lại quá trình mô phỏng hoạt động:
https://drive.google.com/file/d/1GLkhKcINpyprwIQgSbx4_EiQ5BPFjfMI/view?usp=drive_link
