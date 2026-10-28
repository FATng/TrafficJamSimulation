using UnityEngine;

namespace TrafficJamSimulation
{
    /// <summary>
    /// Điều khiển hành vi của người đi bộ qua đường (dạng khối chữ nhật):
    /// - Di chuyển ngang qua lòng đường (trục X) từ lề này sang lề đối diện.
    /// - Có Raycast phía trước: nếu có xe chắn hoặc đang chạy tới thì dừng lại nhường đường, tránh đâm vào xe.
    /// - Khi bị xe đâm trúng: lập tức chuyển màu đỏ rực và đứng yên trong vài giây trước khi tiếp tục.
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

        [Header("Vehicle Detection Raycast (Tránh đâm vào xe)")]
        [Tooltip("Khoảng cách quét xe phía trước mặt người đi bộ (m)")]
        public float vehicleLookAheadDistance = 2.5f;

        [Tooltip("Trạng thái đang dừng chờ xe vượt qua")]
        [SerializeField] private bool isYieldingToVehicle = false;

        [Header("Detour / Bypass Settings (Né vòng qua xe dừng)")]
        [Tooltip("Tốc độ dịch ngang (trục Z) khi đi vòng qua xe dừng chắn đường (m/s)")]
        public float detourSpeed = 1.2f;

        [Tooltip("Trạng thái đang dịch trái/phải để vượt qua xe dừng")]
        [SerializeField] private bool isDetouring = false;

        [Header("Crash / Collision Reaction (Tô đỏ và dừng lại)")]
        [Tooltip("Thời gian đứng yên sau khi va chạm với xe (giây)")]
        public float crashPauseDuration = 3.5f;

        [Tooltip("Màu cảnh báo khi bị đâm va (Đỏ rực)")]
        public Color crashColor = new Color(1.0f, 0.05f, 0.05f, 1f);

        [SerializeField] private bool isInCrashState = false;

        [Header("Visual Animation (Procedural Bobbing)")]
        [Tooltip("Tần số bước chân (chu kỳ bước/giây)")]
        public float stepFrequency = 4.0f;

        [Tooltip("Biên độ nhấp nhô bước chân (m)")]
        public float stepBobAmplitude = 0.04f;

        [Header("State")]
        [SerializeField] private bool hasCompletedCrossing = false;

        private float _baseHeightY = 0.9f;
        private float _startZ = 0f;
        private float _walkTimer = 0f;
        private float _crashTimer = 0f;
        private float _postCrashGraceTimer = 0f;
        private float _detourTimer = 0f;
        private float _detourDirectionZ = 0f;
        private GameObject _lastCollidedVehicle = null;
        private Rigidbody _rb;
        private Renderer _renderer;
        private MaterialPropertyBlock _propBlock;
        private Color _normalColor = new Color(0.95f, 0.72f, 0.12f, 1f);

        public bool HasCompletedCrossing => hasCompletedCrossing;
        public bool IsYieldingToVehicle => isYieldingToVehicle;
        public bool IsDetouring => isDetouring;
        public bool IsInCrashState => isInCrashState;

        private void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            if (_rb != null)
            {
                _rb.useGravity = false;
                _rb.isKinematic = true;
            }

            // Gán PhysicsMaterial triệt tiêu ma sát (zero friction) để người không bao giờ bị bám dính hay kéo theo xe
            Collider col = GetComponent<Collider>();
            if (col != null)
            {
                PhysicsMaterial zeroFric = new PhysicsMaterial("PedestrianZeroFriction")
                {
                    dynamicFriction = 0f,
                    staticFriction = 0f,
                    bounciness = 0f,
                    frictionCombine = PhysicsMaterialCombine.Minimum,
                    bounceCombine = PhysicsMaterialCombine.Minimum
                };
                col.material = zeroFric;
            }

            _renderer = GetComponent<Renderer>();
            _propBlock = new MaterialPropertyBlock();

            if (!CompareTag("Pedestrian"))
            {
                gameObject.tag = "Pedestrian";
            }
        }

        private void Start()
        {
            _baseHeightY = transform.position.y;
            _startZ = transform.position.z;
            if (_baseHeightY < 0.1f) _baseHeightY = 0.9f;
            UpdateFacingRotation();
        }

        /// <summary>
        /// Khởi tạo thông số người qua đường khi spawn
        /// </summary>
        public void Initialize(Vector3 startPos, int direction, float speed, float width)
        {
            transform.position = startPos;
            crossingDirection = (direction >= 0) ? 1 : -1;
            walkingSpeed = speed;
            roadWidth = width;
            targetX = (crossingDirection > 0) ? (roadWidth * 0.5f + 1.2f) : (-roadWidth * 0.5f - 1.2f);
            _baseHeightY = startPos.y;
            _startZ = startPos.z;
            if (_baseHeightY < 0.1f) _baseHeightY = 0.9f;
            hasCompletedCrossing = false;
            isInCrashState = false;
            isDetouring = false;
            _detourTimer = 0f;
            _crashTimer = 0f;
            UpdateFacingRotation();
        }

        private void UpdateFacingRotation()
        {
            float yaw = (crossingDirection > 0) ? 90f : -90f;
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        }

        private void Update()
        {
            if (hasCompletedCrossing) return;

            float dt = Time.deltaTime;

            // 1. Xử lý trạng thái va chạm (Tô đỏ và dừng lại)
            if (isInCrashState)
            {
                _crashTimer -= dt;
                if (_crashTimer <= 0f)
                {
                    isInCrashState = false;
                    ApplyColor(_normalColor); // Khôi phục màu bình thường

                    // SAU KHOẢNG CHỜ: Lùi người xa xe ra và tiếp tục di chuyển, tránh dừng lặp vô thời hạn
                    Vector3 sepPos = transform.position;

                    // Dời người dạt mạnh sang hướng qua đường (+X hoặc -X) để thoát khỏi đầu xe
                    sepPos.x += crossingDirection * 1.8f;

                    // Nếu có xe va chạm: dời người theo trục Z ra xa khỏi đầu xe để tách hẳn hai Collider
                    if (_lastCollidedVehicle != null)
                    {
                        float dz = transform.position.z - _lastCollidedVehicle.transform.position.z;
                        float zDir = (dz >= 0f) ? 1.0f : -1.0f;
                        sepPos.z += zDir * 1.5f;
                    }
                    else
                    {
                        sepPos.z += 1.2f;
                    }

                    transform.position = sepPos;

                    // Đặt thời gian ân hạn 3.5s: Người không bị đứng sững lại bởi chính xe vừa va chạm
                    _postCrashGraceTimer = 3.5f;
                }
                return; // Đứng yên hoàn toàn khi đang bị đâm
            }

            if (_postCrashGraceTimer > 0f)
            {
                _postCrashGraceTimer -= dt;
            }

            // 2. Dò quét xe phía trước mặt bằng Raycast:
            // - Nếu xe đang chạy (speed >= 0.5 m/s): dừng lại nhường đường, tránh đâm vào xe.
            // - Nếu xe chắn trước mặt ĐÃ DỪNG LẠI (< 0.5 m/s): cho phép tiếp tục đi qua hoặc dịch trái/phải để vòng qua xe!
            if (_postCrashGraceTimer <= 0f)
            {
                bool hasVehicleAhead = ScanForVehicles(out bool isStoppedCar, out GameObject carObj, out float hitDist);

                if (hasVehicleAhead)
                {
                    if (!isStoppedCar)
                    {
                        // XE ĐANG CHẠY: Dừng lại nhường đường an toàn
                        isYieldingToVehicle = true;
                        isDetouring = false;
                        _detourTimer = 0f;
                        return;
                    }
                    else
                    {
                        // XE ĐÃ DỪNG LẠI:
                        // Cho phép người đi đường tiếp tục đi qua hoặc dịch trái/phải để vượt qua!
                        isYieldingToVehicle = false;

                        // Tạm thời bỏ qua va chạm cứng để người đi qua mượt mà không bị kẹt mép Collider
                        if (carObj != null)
                        {
                            IgnoreCollisionWithVehicle(carObj, 2.5f);
                        }

                        if (hitDist <= 1.5f)
                        {
                            // Xe chắn quá sát ngay trước mặt (<= 1.5m): Kích hoạt dịch trái / dịch phải (theo trục Z)
                            isDetouring = true;
                            _detourTimer = 0.9f;

                            float carCenterZ = carObj.transform.position.z;
                            // Nếu người đang ở phía trước xe (Z >= carCenterZ): dịch tiến (+Z) qua mũi xe
                            // Nếu người đang ở phía sau xe (Z < carCenterZ): dịch lùi (-Z) qua đuôi xe
                            _detourDirectionZ = (transform.position.z >= carCenterZ) ? 1.0f : -1.0f;
                        }
                        else
                        {
                            // Còn khoảng trống (> 1.5m): Tiếp tục đi thẳng băng qua trước hoặc sau xe
                            if (_detourTimer <= 0f) isDetouring = false;
                        }
                    }
                }
                else
                {
                    isYieldingToVehicle = false;
                    if (_detourTimer <= 0f) isDetouring = false;
                }
            }
            else
            {
                isYieldingToVehicle = false;
                if (_detourTimer <= 0f) isDetouring = false;
            }

            // 3. Di chuyển người đi bộ:
            _walkTimer += dt;
            Vector3 pos = transform.position;

            // Bước ngang qua đường (trục X)
            pos.x += crossingDirection * walkingSpeed * dt;

            // Xử lý dịch trái/phải (trục Z) khi đi vòng qua xe dừng
            if (_detourTimer > 0f)
            {
                _detourTimer -= dt;
                pos.z += _detourDirectionZ * detourSpeed * dt;
            }
            else
            {
                isDetouring = false;
                // Sau khi đã vượt qua xe, dần dần trở lại đường đi ban đầu
                if (Mathf.Abs(pos.z - _startZ) > 0.05f)
                {
                    pos.z = Mathf.MoveTowards(pos.z, _startZ, detourSpeed * 0.4f * dt);
                }
            }

            // 4. Hiệu ứng nhấp nhô nhẹ mô phỏng nhịp bước chân
            float bobOffset = Mathf.Sin(_walkTimer * stepFrequency * Mathf.PI) * stepBobAmplitude;
            pos.y = _baseHeightY + Mathf.Abs(bobOffset);

            transform.position = pos;

            // 5. Kiểm tra đã đến đích (bước qua mép đường bên kia)
            bool reached = (crossingDirection > 0 && pos.x >= targetX) ||
                           (crossingDirection < 0 && pos.x <= targetX);

            if (reached)
            {
                hasCompletedCrossing = true;
                if (destroyOnArrival)
                {
                    Destroy(gameObject, 1.5f);
                }
            }
        }

        /// <summary>
        /// Bắn các tia Raycast hình quạt theo hướng nhìn của người đi bộ để phát hiện xe ô tô cắt ngang.
        /// Đồng thời phân loại xe đang di chuyển hay xe đã dừng lại để người chủ động ứng xử bằng code logic.
        /// </summary>
        private bool ScanForVehicles(out bool isStoppedCar, out GameObject detectedCar, out float hitDist)
        {
            isStoppedCar = false;
            detectedCar = null;
            hitDist = 50f;

            Vector3 forwardDir = transform.forward; // Đã hướng về phía di chuyển (+X hoặc -X)
            Vector3 originCenter = transform.position;
            originCenter.y = 0.6f; // Tầm cao thân xe ô tô

            // 5 tia quét hình quạt: giữa, ±10 độ, ±20 độ để bao quát toàn diện góc nhìn phía trước
            float[] angles = new float[] { 0f, 10f, -10f, 20f, -20f };

            for (int i = 0; i < angles.Length; i++)
            {
                Vector3 rayDir = Quaternion.Euler(0, angles[i], 0) * forwardDir;
                if (Physics.Raycast(originCenter, rayDir, out RaycastHit hit, vehicleLookAheadDistance))
                {
                    if (hit.collider.gameObject != gameObject)
                    {
                        if (hit.collider.CompareTag("Vehicle") || hit.collider.GetComponentInParent<AIFVehicleAgent>() != null)
                        {
                            if (hit.distance < hitDist)
                            {
                                hitDist = hit.distance;
                                detectedCar = hit.collider.gameObject;
                            }
                        }
                    }
                }
            }

            if (detectedCar != null)
            {
                // Kiểm tra vận tốc di chuyển của xe phát hiện được
                float carSpeed = 0f;
                var carAgent = detectedCar.GetComponentInParent<AIFVehicleAgent>();
                if (carAgent != null)
                {
                    carSpeed = carAgent.currentSpeed;
                }
                else
                {
                    var rb = detectedCar.GetComponentInParent<Rigidbody>();
                    if (rb != null) carSpeed = rb.linearVelocity.magnitude;
                }

                // Nếu xe chạy rất chậm hoặc đã dừng hẳn (< 0.5 m/s)
                if (carSpeed < 0.5f)
                {
                    isStoppedCar = true;
                }

                return true;
            }

            return false;
        }

        private void OnDrawGizmosSelected()
        {
            Vector3 forwardDir = transform.forward;
            Vector3 originCenter = transform.position;
            originCenter.y = 0.6f;

            Gizmos.color = isYieldingToVehicle ? Color.red : (isDetouring ? Color.magenta : Color.green);
            float[] angles = new float[] { 0f, 10f, -10f, 20f, -20f };
            for (int i = 0; i < angles.Length; i++)
            {
                Vector3 rayDir = Quaternion.Euler(0, angles[i], 0) * forwardDir;
                Gizmos.DrawRay(originCenter, rayDir * vehicleLookAheadDistance);
            }
        }

        /// <summary>
        /// Kích hoạt trạng thái bị đâm va: tô đỏ rực và đứng yên trong vài giây
        /// </summary>
        public void TriggerCrash(float pauseDuration = 3.5f, GameObject vehicle = null)
        {
            isInCrashState = true;
            _crashTimer = pauseDuration;
            if (vehicle != null)
            {
                _lastCollidedVehicle = vehicle;
                IgnoreCollisionWithVehicle(vehicle, pauseDuration + 3.0f);
            }
            ApplyColor(crashColor);
        }

        /// <summary>
        /// Đổi màu trang phục của khối người đi bộ
        /// </summary>
        public void SetRandomColor(Color bodyColor)
        {
            _normalColor = bodyColor;
            if (!isInCrashState)
            {
                ApplyColor(_normalColor);
            }
        }

        private void ApplyColor(Color c)
        {
            if (_renderer == null) _renderer = GetComponent<Renderer>();
            if (_renderer != null)
            {
                _renderer.GetPropertyBlock(_propBlock);
                _propBlock.SetColor("_Color", c);
                _propBlock.SetColor("_BaseColor", c);
                _renderer.SetPropertyBlock(_propBlock);
            }
        }

        private void OnCollisionEnter(Collision collision)
        {
            HandleVehicleCollision(collision.gameObject);
        }

        private void OnTriggerEnter(Collider other)
        {
            HandleVehicleCollision(other.gameObject);
        }

        private void HandleVehicleCollision(GameObject other)
        {
            // Nếu đang trong thời gian ân hạn sau va chạm: bỏ qua kích hoạt lại va chạm tránh dừng lặp vô thời hạn
            if (_postCrashGraceTimer > 0f) return;

            if (other.CompareTag("Vehicle") || other.GetComponentInParent<AIFVehicleAgent>() != null)
            {
                // Vô hiệu hóa va chạm vật lý giữa người và xe ngay lập tức để không bị kéo/kẹt
                IgnoreCollisionWithVehicle(other, crashPauseDuration + 3.0f);

                // Bị xe đâm trúng: Người đổi đỏ rực và dừng lại
                TriggerCrash(crashPauseDuration, other);

                // Đồng thời kích hoạt xe đổi màu đỏ rực và dừng lại
                var carVis = other.GetComponentInParent<CarCollisionVisualizer>();
                if (carVis != null)
                {
                    carVis.TriggerCrash(crashPauseDuration);
                }

                var carAgent = other.GetComponentInParent<AIFVehicleAgent>();
                if (carAgent != null)
                {
                    carAgent.NotifyPedestrianCollision(gameObject);
                }
            }
        }

        /// <summary>
        /// Tạm thời bỏ qua va chạm vật lý giữa người và xe để hoàn toàn triệt tiêu lực kéo/đẩy vật lý
        /// </summary>
        public void IgnoreCollisionWithVehicle(GameObject vehicleObj, float duration)
        {
            if (vehicleObj == null) return;
            StartCoroutine(IgnoreCollisionRoutine(vehicleObj, duration));
        }

        private System.Collections.IEnumerator IgnoreCollisionRoutine(GameObject vehicleObj, float duration)
        {
            if (vehicleObj == null) yield break;
            Collider[] myCols = GetComponentsInChildren<Collider>();
            Collider[] otherCols = vehicleObj.GetComponentsInChildren<Collider>();

            foreach (var m in myCols)
            {
                foreach (var o in otherCols)
                {
                    if (m != null && o != null) Physics.IgnoreCollision(m, o, true);
                }
            }

            yield return new WaitForSeconds(duration);

            if (vehicleObj != null)
            {
                foreach (var m in myCols)
                {
                    foreach (var o in otherCols)
                    {
                        if (m != null && o != null) Physics.IgnoreCollision(m, o, false);
                    }
                }
            }
        }
    }
}
