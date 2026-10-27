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
        private float _walkTimer = 0f;
        private float _crashTimer = 0f;
        private float _postCrashGraceTimer = 0f;
        private GameObject _lastCollidedVehicle = null;
        private Rigidbody _rb;
        private Renderer _renderer;
        private MaterialPropertyBlock _propBlock;
        private Color _normalColor = new Color(0.95f, 0.72f, 0.12f, 1f);

        public bool HasCompletedCrossing => hasCompletedCrossing;
        public bool IsYieldingToVehicle => isYieldingToVehicle;
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
            if (_baseHeightY < 0.1f) _baseHeightY = 0.9f;
            hasCompletedCrossing = false;
            isInCrashState = false;
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

            // 2. Dò quét xe phía trước mặt bằng Raycast: nếu có xe đang đi tới hoặc chắn đường thì dừng lại
            // Nếu đang trong thời gian ân hạn sau va chạm: bỏ qua quét dừng xe để người dứt khoát bước đi thoát kẹt!
            if (_postCrashGraceTimer <= 0f)
            {
                isYieldingToVehicle = ScanForVehiclesAhead();
                if (isYieldingToVehicle)
                {
                    // Tạm dừng bước đi, giữ nguyên vị trí, chờ xe chạy qua
                    return;
                }
            }
            else
            {
                isYieldingToVehicle = false;
            }

            // 3. Di chuyển ngang qua đường theo trục X
            _walkTimer += dt;
            Vector3 pos = transform.position;
            pos.x += crossingDirection * walkingSpeed * dt;

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
        /// Bắn các tia Raycast theo hướng nhìn của người đi bộ để phát hiện xe ô tô cắt ngang
        /// </summary>
        private bool ScanForVehiclesAhead()
        {
            Vector3 forwardDir = transform.forward; // Đã hướng về phía di chuyển (+X hoặc -X)
            Vector3 originCenter = transform.position;
            originCenter.y = 0.6f; // Tầm cao thân xe ô tô

            // 3 tia: giữa, chếch trái, chếch phải một góc nhỏ 15 độ để bao quát góc nhìn
            Vector3[] rayDirs = new Vector3[]
            {
                forwardDir,
                Quaternion.Euler(0, 15f, 0) * forwardDir,
                Quaternion.Euler(0, -15f, 0) * forwardDir
            };

            for (int i = 0; i < rayDirs.Length; i++)
            {
                if (Physics.Raycast(originCenter, rayDirs[i], out RaycastHit hit, vehicleLookAheadDistance))
                {
                    if (hit.collider.gameObject != gameObject)
                    {
                        if (hit.collider.CompareTag("Vehicle") || hit.collider.GetComponentInParent<AIFVehicleAgent>() != null)
                        {
                            return true; // Có xe trước mặt, dừng lại nhường đường
                        }
                    }
                }
            }

            return false;
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
