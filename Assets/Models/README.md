# Assets/Models

Thư mục này dùng để lưu trữ các tệp mô hình trí tuệ nhân tạo định dạng **`.onnx`** (Open Neural Network Exchange).

### Hướng dẫn sử dụng với Unity Sentis:
1. Đặt tệp `.onnx` của bạn (ví dụ: `traffic_policy.onnx`, `car_following_dqn.onnx`) vào thư mục này.
2. Unity Sentis sẽ tự động import file `.onnx` thành asset kiểu **`ModelAsset`**.
3. Kéo `ModelAsset` vào trường `Model Asset` trên component **`TrafficInferenceEngine`** trong Scene.
4. Đổi `Control Mode` của các xe (hoặc `Vehicle Control Mode` trong `TrafficLaneManager`) thành **`SentisInference`**.

### Input / Output chuẩn của `TrafficInferenceEngine`:
- **Input Tensor**: `[1, 3]` gồm:
  - `speed` (Tốc độ hiện tại, chuẩn hóa /30 m/s)
  - `distanceToLeader` (Khoảng cách tới xe liền trước, chuẩn hóa /50 m)
  - `relativeVelocity` (Vận tốc tương đối với xe trước: $v_{leader} - v_{current}$, chuẩn hóa /15 m/s)
- **Output Tensor**: `[1, 1]` gồm giá trị gia tốc dự đoán $a$ ($m/s^2$).
