# Assets/Prefabs

Thư mục này dùng để lưu trữ các mẫu xe (Vehicle Prefabs).

### Cách tạo mẫu xe (Vehicle Prefab):
1. Trong Unity Editor, tạo một GameObject 3D (ví dụ: `3D Object -> Cube` hoặc import 3D model ô tô).
2. Đặt kích thước tiêu chuẩn: khoảng `(x: 2m, y: 1.4m, z: 4.2m)`.
3. Gắn component **`VehicleAgent`** vào GameObject:
   - Component này chịu trách nhiệm nhận lệnh gia tốc, cập nhật vị trí làn đường và hiển thị màu (Xanh = lưu thông tốt, Đỏ = kẹt xe).
4. Kéo GameObject từ cửa sổ **Hierarchy** vào thư mục `Assets/Prefabs/` để tạo file Prefab (ví dụ: `CarPrefab.prefab`).
5. Gán Prefab vừa tạo vào trường `Vehicle Prefab` của GameObject mang component **`TrafficLaneManager`**.
   *(Lưu ý: Nếu không gán prefab, hệ thống sẽ tự sinh các hình khối Cube 3D mặc định để mô phỏng ngay lập tức)*.
