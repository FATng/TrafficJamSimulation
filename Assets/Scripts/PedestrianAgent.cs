using UnityEngine;

namespace TrafficJamSimulation
{
    /// <summary>
    /// Điều khiển hành vi của người đi bộ băng qua đường:
    /// - Di chuyển ngang qua lòng đường (trục X) từ lề này sang lề đối diện.
    /// - Tự động quay mặt theo hướng di chuyển (+X hoặc -X).
    /// - Tạo hiệu ứng nhấp nhô (bobbing) khi bước đi giúp nhân vật sinh động.
    /// - Tự động hủy (despawn) sau khi đã qua đường an toàn.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class PedestrianAgent : MonoBehaviour
    {
        [Header("Crossing Settings")]
        [Tooltip("Tốc độ đi bộ (m/s), thường từ 1.2m/s đến 1.8m/s (~4.5 - 6.5 km/h)")]
        public float walkingSpeed = 1.4f;

        [Tooltip("Hướng di chuyển theo trục X (+1: từ trái sang phải, -1: từ phải sang trái)")]
        public int crossingDirection = 1;

        [Tooltip("Độ rộng mặt đường (m)")]
        public float roadWidth = 10f;

        [Tooltip("Tọa độ X đích đến sau khi băng qua đường")]
        public float targetX = 5.5f;

        [Tooltip("Tự động hủy GameObject sau khi qua đường an toàn")]
        public bool destroyOnArrival = true;

        [Header("Visual Animation (Procedural Bobbing)")]
        [Tooltip("Tần số bước chân (chu kỳ bước/giây)")]
        public float stepFrequency = 4.0f;

        [Tooltip("Biên độ nhấp nhô bước chân (m)")]
        public float stepBobAmplitude = 0.05f;

        [Header("State")]
        [SerializeField] private bool hasCompletedCrossing = false;
        private float _baseHeightY = 0f;
        private float _walkTimer = 0f;
        private Rigidbody _rb;

        public bool HasCompletedCrossing => hasCompletedCrossing;

        private void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            if (_rb != null)
            {
                _rb.useGravity = false;
                _rb.isKinematic = true; // Đi bộ mượt mà không bị tác động quán tính ngoại lực làm lệch
            }

            // Đảm bảo tag Pedestrian
            if (!CompareTag("Pedestrian"))
            {
                gameObject.tag = "Pedestrian";
            }
        }

        private void Start()
        {
            _baseHeightY = transform.position.y;
            UpdateFacingRotation();
        }

        /// <summary>
        /// Khởi tạo thông số người qua đường khi spawn
        /// </summary>
        /// <param name="startPos">Vị trí bắt đầu xuất phát</param>
        /// <param name="direction">+1 nếu từ trái sang phải, -1 nếu từ phải sang trái</param>
        /// <param name="speed">Tốc độ đi bộ</param>
        /// <param name="width">Bề rộng mặt đường</param>
        public void Initialize(Vector3 startPos, int direction, float speed, float width)
        {
            transform.position = startPos;
            crossingDirection = (direction >= 0) ? 1 : -1;
            walkingSpeed = speed;
            roadWidth = width;
            targetX = (crossingDirection > 0) ? (roadWidth * 0.5f + 1.2f) : (-roadWidth * 0.5f - 1.2f);
            _baseHeightY = startPos.y;
            hasCompletedCrossing = false;
            UpdateFacingRotation();
        }

        private void UpdateFacingRotation()
        {
            // Quay mặt về hướng di chuyển (+X: góc 90 độ, -X: góc -90 độ)
            float yaw = (crossingDirection > 0) ? 90f : -90f;
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        }

        private void Update()
        {
            if (hasCompletedCrossing) return;

            float dt = Time.deltaTime;
            _walkTimer += dt;

            // 1. Di chuyển ngang qua đường theo trục X
            Vector3 pos = transform.position;
            pos.x += crossingDirection * walkingSpeed * dt;

            // 2. Hiệu ứng nhấp nhô nhẹ mô phỏng nhịp bước chân
            float bobOffset = Mathf.Sin(_walkTimer * stepFrequency * Mathf.PI) * stepBobAmplitude;
            pos.y = _baseHeightY + Mathf.Abs(bobOffset);

            transform.position = pos;

            // 3. Kiểm tra đã đến đích (bước qua mép đường bên kia)
            bool reached = (crossingDirection > 0 && pos.x >= targetX) ||
                           (crossingDirection < 0 && pos.x <= targetX);

            if (reached)
            {
                hasCompletedCrossing = true;
                if (destroyOnArrival)
                {
                    Destroy(gameObject, 1.5f); // Chờ 1.5s để bước hẳn vào lề rồi biến mất
                }
            }
        }

        /// <summary>
        /// Đổi màu trang phục ngẫu nhiên để các người đi bộ trông đa dạng
        /// </summary>
        public void SetRandomColor(Color bodyColor)
        {
            var renderers = GetComponentsInChildren<Renderer>();
            foreach (var rend in renderers)
            {
                // Chỉ đổi màu phần thân (Body)
                if (rend.gameObject.name.ToLower().Contains("body") || rend.gameObject == gameObject)
                {
                    rend.material.color = bodyColor;
                }
            }
        }
    }
}
