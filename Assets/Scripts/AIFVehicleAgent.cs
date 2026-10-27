using UnityEngine;
using Unity.InferenceEngine;
using TrafficJamSimulation;
using Random = UnityEngine.Random;

public class AIFVehicleAgent : MonoBehaviour
{
    [SerializeField] private ModelAsset onnxModelAsset;
    [SerializeField] private float targetSpeed = 15f;
    [SerializeField] private float maxAcceleration = 8f;

    [Header("Driver Personality (Độ Vội)")]
    [Range(0.6f, 1.6f)]
    [Tooltip("Độ vội vàng của tài xế: quyết định tốc độ ổn định (targetSpeed), khoảng cách an toàn và gia tốc.")]
    public float hurryFactor = 1.0f;

    [Header("Road Boundary Limits (Ranh Giới Đường)")]
    [Tooltip("Độ rộng cố định của mặt đường (m). Mép đường nằm ở ± roadWidth/2")]
    public float roadWidth = 10f;
    [Tooltip("Khoảng cách tối đa an toàn tính từ tim đường để xe không rơi xuống vực")]
    public float maxSafeRoadX = 4.1f;
    [SerializeField] private float normalizedRoadOffset = 0f;

    [Header("Height Constraint (Cố Định Chiều Cao)")]
    [Tooltip("Chiều cao cố định của xe trên mặt phẳng đường, ngăn tuyệt đối việc bị hất bay lên trời khi va chạm")]
    public float fixedHeightY = 0.5f;

    [Header("Heading Angle Constraint (Khống Chế Góc Quay Tránh Ngược Chiều)")]
    [Tooltip("Góc quay tối đa (độ) quanh trục Y tính từ hướng tiến thẳng (+Z). Xe chỉ được phép quay trong khoảng [-maxHeadingAngle, +maxHeadingAngle], ngăn tuyệt đối việc bị va chạm hoặc thuật toán làm quay đầu ngược chiều.")]
    [Range(5f, 45f)]
    public float maxHeadingAngle = 18f;

    [Header("Runtime State")]
    [SerializeField] private float currentSpeed = 0f;
    [SerializeField] private float effectiveTargetSpeed = 15f;
    [SerializeField] private bool isFrontCarBraking = false;
    [SerializeField] private TurnSignal frontCarTurnSignal = TurnSignal.None;
    [SerializeField] private bool isOpenRoadAhead = false;

    [Header("Lighting")]
    [SerializeField] private CarLightsController lightsController;

    [Header("Collision Effect")]
    [SerializeField] private CarCollisionVisualizer collisionVisualizer;

    [Header("Lane Discipline & Centering (Hệ Thống Làn Đường)")]
    [Tooltip("Tọa độ X của các làn đường (Mặt đường 10m: [-2.4m, 0m, +2.4m])")]
    public float[] laneCenters = new float[] { -2.4f, 0f, 2.4f };
    [SerializeField] private int currentLaneIndex = 1;
    [SerializeField] private int targetLaneIndex = 1;
    [SerializeField] private float targetLaneX = 0f;
    [SerializeField] private bool isChangingLane = false;
    private float laneChangeCooldownTimer = 0f;

    [Header("Side Vehicle Detection & Evasion (Né Xe Đi Song Song)")]
    [Tooltip("Khoảng cách quét xe đi song song hai bên sườn (m)")]
    public float sideScanDistance = 2.8f;
    [Tooltip("Cường độ bẻ lái né sang bên đường trống khi phát hiện xe đi song song sát sườn")]
    public float sideEvasionSteerStrength = 0.40f;

    [Header("Separation & Collision Recovery (Ưu Tiên Tách Xe & Tách Lề)")]
    [Tooltip("Tốc độ đẩy tách nhẹ 2 xe ra khỏi nhau khi va chạm (m/s)")]
    public float vehicleSeparationSpeed = 1.2f;

    [Tooltip("Tốc độ đẩy tách xe ra khỏi lề đường khi va vào lề hoặc rào chắn (m/s)")]
    public float curbSeparationSpeed = 2.5f;

    [Tooltip("Gia tốc phanh khẩn cấp để dừng xe khi đâm va (m/s^2)")]
    public float crashBrakeDecel = 12.0f;

    private bool isCollidingWithVehicle = false;
    private bool isCollidingWithBarrier = false;
    private Vector3 vehicleSeparationVector = Vector3.zero;
    private Vector3 barrierSeparationVector = Vector3.zero;
    private float vehicleCollisionTimer = 0f;
    private float barrierCollisionTimer = 0f;
    private float pedestrianCollisionPauseTimer = 0f;
    private float postPedestrianCollisionGraceTimer = 0f;

    private Model runtimeModel;
    private Worker worker;
    private Rigidbody rb;
    private static int instanceCounter = 0;
    private int myId;
    private float demoSignalTimer = 0f;
    private float nextDemoSignalTime = 5f;
    private float spawnTime = 0f;

    void Start()
    {
        spawnTime = Time.time;
        myId = instanceCounter++;
        rb = GetComponent<Rigidbody>();
        if (rb != null)
        {
            // Cấu hình vật thể vật lý cứng: Khối lượng 1500kg, ContinuousDynamic chống xuyên thủng, Interpolate chống giật
            rb.mass = 1500f;
            rb.useGravity = false;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.constraints = RigidbodyConstraints.FreezePositionY | RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationY | RigidbodyConstraints.FreezeRotationZ;
            Vector3 initPos = rb.position;
            initPos.y = fixedHeightY;
            rb.position = initPos;
            rb.rotation = Quaternion.identity;
        }

        if (lightsController == null)
        {
            lightsController = GetComponent<CarLightsController>();
        }
        if (collisionVisualizer == null)
        {
            collisionVisualizer = GetComponent<CarCollisionVisualizer>();
        }
        nextDemoSignalTime = Random.Range(3f, 8f);

        // Nếu độ vội chưa được thiết lập riêng, tạo độ phân hóa ngẫu nhiên giữa các xe (0.75x đến 1.35x)
        if (Mathf.Approximately(hurryFactor, 1.0f))
        {
            hurryFactor = Random.Range(0.75f, 1.35f);
        }
        // Giảm tốc độ của xe có hurryFactor cao xuống một lượng nhỏ (Lerp 0.6) để xe dễ kiểm soát quán tính
        effectiveTargetSpeed = targetSpeed * Mathf.Lerp(1.0f, hurryFactor, 0.6f);

        // Tự động nhận diện làn đường ban đầu dựa trên vị trí spawn
        float spawnX = transform.position.x;
        if (spawnX < -1.2f) currentLaneIndex = 0;
        else if (spawnX > 1.2f) currentLaneIndex = 2;
        else currentLaneIndex = 1;

        targetLaneIndex = currentLaneIndex;
        if (laneCenters == null || laneCenters.Length == 0)
        {
            laneCenters = new float[] { -2.4f, 0f, 2.4f };
        }
        targetLaneX = laneCenters[currentLaneIndex];
        laneChangeCooldownTimer = Random.Range(2.0f, 5.0f);

        // Áp dụng vật liệu vật lý rắn chắc (không bounciness, ma sát nhẹ)
        Collider[] allCols = GetComponentsInChildren<Collider>();
        PhysicsMaterial solidMat = new PhysicsMaterial("SolidVehicleMaterial")
        {
            dynamicFriction = 0f,
            staticFriction = 0f,
            frictionCombine = PhysicsMaterialCombine.Minimum,
            bounciness = 0f,
            bounceCombine = PhysicsMaterialCombine.Minimum
        };
        foreach (var c in allCols)
        {
            if (c != null) c.material = solidMat;
        }

        // Tốc độ khởi điểm ban đầu khi xuất phát
        currentSpeed = 5f;

#if UNITY_EDITOR
        // Tự động tìm nạp model ONNX nếu prefab chưa gán thủ công
        if (onnxModelAsset == null)
        {
            onnxModelAsset = UnityEditor.AssetDatabase.LoadAssetAtPath<ModelAsset>("Assets/Models/traffic_aif_agent.onnx");
        }
#endif

        if (onnxModelAsset != null)
        {
            try
            {
                runtimeModel = ModelLoader.Load(onnxModelAsset);
                worker = new Worker(runtimeModel, BackendType.CPU);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[AIFVehicleAgent] Không thể khởi tạo worker: {ex.Message}");
                worker = null;
            }
        }
    }

    void FixedUpdate()
    {
        if (rb == null) return;

        float dt = Time.fixedDeltaTime;

        // Nếu vừa xảy ra va chạm với người đi bộ: xe dừng hoàn toàn trong vài giây
        if (pedestrianCollisionPauseTimer > 0f)
        {
            pedestrianCollisionPauseTimer -= dt;
            currentSpeed = 0f;
            if (rb != null) rb.linearVelocity = Vector3.zero;
            if (lightsController != null) lightsController.SetBraking(true);
            if (pedestrianCollisionPauseTimer <= 0f)
            {
                // Sau khi hết thời gian chờ: đặt 2.5s ân hạn để người đi bộ lùi xa và bước tiếp
                postPedestrianCollisionGraceTimer = 2.5f;
            }
            return;
        }

        if (postPedestrianCollisionGraceTimer > 0f)
        {
            postPedestrianCollisionGraceTimer -= dt;
        }

        if (laneChangeCooldownTimer > 0f)
        {
            laneChangeCooldownTimer -= dt;
        }

        // 1. Thu thập trạng thái (Observation Vector)
        effectiveTargetSpeed = targetSpeed * Mathf.Lerp(1.0f, hurryFactor, 0.6f);
        float speedError = (effectiveTargetSpeed - currentSpeed) / Mathf.Max(1f, effectiveTargetSpeed);

        // Chuẩn hóa ranh giới đường: [-1 = mép trái, 0 = tim đường, +1 = mép phải]
        float halfRoadWidth = Mathf.Max(1f, roadWidth * 0.5f);
        normalizedRoadOffset = Mathf.Clamp(transform.position.x / halfRoadWidth, -1f, 1f);

        // Báo cáo vị trí cho hệ thống tính toán Mean Field
        if (TrafficFieldManager.Instance != null)
        {
            TrafficFieldManager.Instance.RegisterVehiclePosition(transform.position);
        }

        // ===================================================================================
        // 1. HỆ THỐNG CẢM BIẾN QUANG HỌC ĐA HƯỚNG TOÀN DIỆN (OMNIDIRECTIONAL PERCEPTION):
        // A. Chùm 9 tia thẳng mặt cản trước (-0.90m đến +0.90m) tầm quét xa 60m
        // B. 2 tia góc nhẹ (±6°) từ hai góc cản trước tầm quét xa 45m
        // C. 8 tia chéo góc đón đầu (±15°, ±30°, ±45°, ±60°) tầm quét 30m
        // D. 4 điểm quét sườn xe mỗi bên (-transform.right & +transform.right) tầm quét 2.8m
        // E. Cảm biến làn lân cận: Quét trước (35m) & quét sau/điểm mù (22m) trên làn Trái/Phải
        // ===================================================================================
        float distanceToFront = 60f;
        isFrontCarBraking = false;
        frontCarTurnSignal = TurnSignal.None;
        GameObject frontVehicleObj = null;

        bool isPedestrianInPath = false;
        float nearestPedDist = 60f;
        float nearestPedRelX = 0f;
        int nearestPedDir = 0;

        Vector3 carCenter = new Vector3(transform.position.x, fixedHeightY, transform.position.z);
        Vector3 frontBumperCenter = carCenter + transform.forward * 2.05f;

        // A. 9 tia thẳng trải đều trên mặt cản trước (-0.80m đến +0.80m) chỉ quét trong phạm vi làn đường của xe
        float[] frontOffsetsX = new float[] { 0f, -0.20f, 0.20f, -0.40f, 0.40f, -0.60f, 0.60f, -0.80f, 0.80f };
        for (int r = 0; r < frontOffsetsX.Length; r++)
        {
            Vector3 origin = frontBumperCenter + transform.right * frontOffsetsX[r];
            if (Physics.Raycast(origin, transform.forward, out RaycastHit hit, 50f))
            {
                if (hit.collider.gameObject != gameObject)
                {
                    bool isVeh = hit.collider.CompareTag("Vehicle") || hit.collider.GetComponentInParent<AIFVehicleAgent>() != null;
                    bool isPed = hit.collider.CompareTag("Pedestrian") || hit.collider.GetComponentInParent<PedestrianAgent>() != null;

                    float hitRelX = hit.point.x - transform.position.x;

                    // Chỉ coi là xe phía trước cùng làn nếu điểm va chạm nằm trong phạm vi lòng làn (|hitRelX| <= 1.05m)
                    // TUYỆT ĐỐI KHÔNG nhận nhầm xe ở làn bên cạnh thành xe phía trước!
                    if (isVeh && Mathf.Abs(hitRelX) <= 1.05f && hit.distance < distanceToFront)
                    {
                        distanceToFront = hit.distance;
                        frontVehicleObj = hit.collider.gameObject;
                    }
                    else if (isPed && hit.distance < nearestPedDist)
                    {
                        isPedestrianInPath = true;
                        nearestPedDist = hit.distance;
                        nearestPedRelX = hitRelX;
                        var pAgent = hit.collider.GetComponentInParent<PedestrianAgent>();
                        if (pAgent != null) nearestPedDir = pAgent.crossingDirection;
                    }
                }
            }
        }

        // C. 8 tia quét chéo góc đón đầu (±15°, ±30°, ±45°, ±60°) tầm xa 30m
        bool hasCarOnLeftFrontDiag = false;
        bool hasCarOnRightFrontDiag = false;

        float[] diagAngles = new float[] { -60f, -45f, -30f, -15f, 15f, 30f, 45f, 60f };
        for (int d = 0; d < diagAngles.Length; d++)
        {
            Vector3 diagDir = Quaternion.Euler(0f, diagAngles[d], 0f) * transform.forward;
            if (Physics.Raycast(frontBumperCenter, diagDir, out RaycastHit diagHit, 30f))
            {
                if (diagHit.collider.gameObject != gameObject)
                {
                    bool isPed = diagHit.collider.CompareTag("Pedestrian") || diagHit.collider.GetComponentInParent<PedestrianAgent>() != null;
                    bool isVeh = diagHit.collider.CompareTag("Vehicle") || diagHit.collider.GetComponentInParent<AIFVehicleAgent>() != null;

                    if (isPed)
                    {
                        float pedDistZ = diagHit.point.z - transform.position.z;
                        float pedDistX = diagHit.point.x - transform.position.x;

                        if (pedDistZ > 0.5f && pedDistZ < 30f)
                        {
                            var pedAgent = diagHit.collider.GetComponentInParent<PedestrianAgent>();
                            int pedDir = (pedAgent != null) ? pedAgent.crossingDirection : (pedDistX > 0 ? -1 : 1);
                            float pedSpeed = (pedAgent != null) ? pedAgent.walkingSpeed : 1.4f;

                            float timeToMeet = pedDistZ / Mathf.Max(2.0f, currentSpeed);
                            float predictedPedX = diagHit.point.x + (pedDir * pedSpeed * timeToMeet);
                            float predictedRelX = predictedPedX - transform.position.x;

                            if (Mathf.Abs(predictedRelX) < 2.5f || Mathf.Abs(pedDistX) < 2.2f)
                            {
                                isPedestrianInPath = true;
                                if (pedDistZ < nearestPedDist)
                                {
                                    nearestPedDist = pedDistZ;
                                    nearestPedRelX = pedDistX;
                                    nearestPedDir = pedDir;
                                }
                            }
                        }
                    }
                    else if (isVeh)
                    {
                        if (diagHit.distance < 8.0f)
                        {
                            if (diagAngles[d] < 0f) hasCarOnLeftFrontDiag = true;
                            else hasCarOnRightFrontDiag = true;
                        }
                    }
                }
            }
        }

        // D. 4 điểm quét sườn xe mỗi bên (-transform.right & +transform.right)
        bool hasCarOnLeft = false;
        bool hasCarOnRight = false;
        float distToCarLeft = 50f;
        float distToCarRight = 50f;
        float halfWidth = 0.95f;
        float[] sideOffsetsZ = new float[] { 1.8f, 0.6f, -0.6f, -1.8f };

        for (int s = 0; s < sideOffsetsZ.Length; s++)
        {
            Vector3 leftOrigin = carCenter + transform.forward * sideOffsetsZ[s] - transform.right * halfWidth;
            if (Physics.Raycast(leftOrigin, -transform.right, out RaycastHit hitL, sideScanDistance))
            {
                if (hitL.collider.gameObject != gameObject && (hitL.collider.CompareTag("Vehicle") || hitL.collider.GetComponentInParent<AIFVehicleAgent>() != null))
                {
                    hasCarOnLeft = true;
                    if (hitL.distance < distToCarLeft) distToCarLeft = hitL.distance;
                }
            }

            Vector3 rightOrigin = carCenter + transform.forward * sideOffsetsZ[s] + transform.right * halfWidth;
            if (Physics.Raycast(rightOrigin, transform.right, out RaycastHit hitR, sideScanDistance))
            {
                if (hitR.collider.gameObject != gameObject && (hitR.collider.CompareTag("Vehicle") || hitR.collider.GetComponentInParent<AIFVehicleAgent>() != null))
                {
                    hasCarOnRight = true;
                    if (hitR.distance < distToCarRight) distToCarRight = hitR.distance;
                }
            }
        }

        // E. Cảm biến làn lân cận: Quét trước (35m) và quét sau/điểm mù (22m) trên làn Trái/Phải
        float distLeftLaneAhead = 50f;
        float distLeftLaneBehind = 50f;
        float distRightLaneAhead = 50f;
        float distRightLaneBehind = 50f;

        if (currentLaneIndex > 0)
        {
            float leftLaneX = laneCenters[currentLaneIndex - 1];
            Vector3 leftLaneOrigin = new Vector3(leftLaneX, fixedHeightY, transform.position.z);

            if (Physics.Raycast(leftLaneOrigin, Vector3.forward, out RaycastHit hitLAhead, 35f))
            {
                if (hitLAhead.collider.gameObject != gameObject && (hitLAhead.collider.CompareTag("Vehicle") || hitLAhead.collider.GetComponentInParent<AIFVehicleAgent>() != null))
                {
                    distLeftLaneAhead = hitLAhead.distance;
                }
            }
            if (Physics.Raycast(leftLaneOrigin, Vector3.back, out RaycastHit hitLBehind, 22f))
            {
                if (hitLBehind.collider.gameObject != gameObject && (hitLBehind.collider.CompareTag("Vehicle") || hitLBehind.collider.GetComponentInParent<AIFVehicleAgent>() != null))
                {
                    distLeftLaneBehind = hitLBehind.distance;
                }
            }
        }

        if (currentLaneIndex < 2)
        {
            float rightLaneX = laneCenters[currentLaneIndex + 1];
            Vector3 rightLaneOrigin = new Vector3(rightLaneX, fixedHeightY, transform.position.z);

            if (Physics.Raycast(rightLaneOrigin, Vector3.forward, out RaycastHit hitRAhead, 35f))
            {
                if (hitRAhead.collider.gameObject != gameObject && (hitRAhead.collider.CompareTag("Vehicle") || hitRAhead.collider.GetComponentInParent<AIFVehicleAgent>() != null))
                {
                    distRightLaneAhead = hitRAhead.distance;
                }
            }
            if (Physics.Raycast(rightLaneOrigin, Vector3.back, out RaycastHit hitRBehind, 22f))
            {
                if (hitRBehind.collider.gameObject != gameObject && (hitRBehind.collider.CompareTag("Vehicle") || hitRBehind.collider.GetComponentInParent<AIFVehicleAgent>() != null))
                {
                    distRightLaneBehind = hitRBehind.distance;
                }
            }
        }

        // Quét phía sau làn hiện tại để kiểm tra xe sau bám gần
        float distOwnLaneBehind = 50f;
        Vector3 ownRearOrigin = carCenter - transform.forward * 2.0f;
        if (Physics.Raycast(ownRearOrigin, -transform.forward, out RaycastHit hitOwnBehind, 25f))
        {
            if (hitOwnBehind.collider.gameObject != gameObject && (hitOwnBehind.collider.CompareTag("Vehicle") || hitOwnBehind.collider.GetComponentInParent<AIFVehicleAgent>() != null))
            {
                distOwnLaneBehind = hitOwnBehind.distance;
            }
        }

        // Thông số xe phía trước & vận tốc tiếp cận (Closing Speed)
        float leaderSpeed = 0f;
        bool isLeaderStopped = false;

        if (frontVehicleObj != null)
        {
            AIFVehicleAgent leaderAgent = frontVehicleObj.GetComponentInParent<AIFVehicleAgent>();
            if (leaderAgent != null)
            {
                leaderSpeed = leaderAgent.currentSpeed;
            }
            else
            {
                Rigidbody lRb = frontVehicleObj.GetComponentInParent<Rigidbody>();
                if (lRb != null) leaderSpeed = lRb.linearVelocity.z;
            }

            CarLightsController frontLights = frontVehicleObj.GetComponentInParent<CarLightsController>();
            if (frontLights != null)
            {
                isFrontCarBraking = frontLights.IsBraking;
                frontCarTurnSignal = frontLights.CurrentSignal;
            }

            if (leaderSpeed < 2.0f) isLeaderStopped = true;
        }

        float closingSpeed = (frontVehicleObj != null) ? Mathf.Max(0f, currentSpeed - leaderSpeed) : 0f;
        isOpenRoadAhead = (distanceToFront >= 16f && !isPedestrianInPath);

        // ===================================================================================
        // 2. ĐÁNH GIÁ AN TOÀN CHUYỂN LÀN & NÉ VẬT CẢN (LANE DISCIPLINE & EVASION):
        // ===================================================================================
        float currentPosX = transform.position.x;

        // Điều kiện bên cạnh thông thoáng để lách sang:
        // Làn bên cạnh không có xe ép sát sườn (distToCar >= 0.50m), khoảng trống phía trước (>= 7.0m) và phía sau (>= 5.0m)
        bool canGoLeft = (currentLaneIndex > 0) &&
                         (distToCarLeft >= 0.50f) &&
                         !hasCarOnLeftFrontDiag &&
                         (distLeftLaneAhead > 7.0f) &&
                         (distLeftLaneBehind > 5.0f) &&
                         (currentPosX > -3.2f);

        bool canGoRight = (currentLaneIndex < 2) &&
                          (distToCarRight >= 0.50f) &&
                          !hasCarOnRightFrontDiag &&
                          (distRightLaneAhead > 7.0f) &&
                          (distRightLaneBehind > 5.0f) &&
                          (currentPosX < 3.2f);

        // ĐIỀU KIỆN CHỦ ĐỘNG NÉ NGƯỜI QUA ĐƯỜNG:
        // Chỉ chủ động né khi phía trước và phía sau không có xe khác ở gần.
        // Nếu có xe khác thì tuyệt đối không tùy tiện lách làn mà ưu tiên dừng lại nhường đường.
        bool isOwnLaneIsolated = (frontVehicleObj == null || distanceToFront >= 14.0f) && (distOwnLaneBehind >= 10.0f);

        bool isLeftLaneSafeForPedEvade = (currentLaneIndex > 0) &&
                                        (distToCarLeft >= 1.0f) &&
                                        !hasCarOnLeftFrontDiag &&
                                        (distLeftLaneAhead >= 14.0f) &&
                                        (distLeftLaneBehind >= 10.0f) &&
                                        (currentPosX > -3.2f);

        bool isRightLaneSafeForPedEvade = (currentLaneIndex < 2) &&
                                         (distToCarRight >= 1.0f) &&
                                         !hasCarOnRightFrontDiag &&
                                         (distRightLaneAhead >= 14.0f) &&
                                         (distRightLaneBehind >= 10.0f) &&
                                         (currentPosX < 3.2f);

        bool canEvadePedestrian = isOwnLaneIsolated && (isLeftLaneSafeForPedEvade || isRightLaneSafeForPedEvade);

        bool hasPedestrianObstacle = (isPedestrianInPath && nearestPedDist < 26.0f);
        bool hasStoppedVehicleObstacle = (frontVehicleObj != null && isLeaderStopped && distanceToFront < 18.0f);
        bool hasSlowVehicleObstacle = (frontVehicleObj != null && !isLeaderStopped && distanceToFront < 14.0f && leaderSpeed < effectiveTargetSpeed * 0.70f);
        bool hasObstacleAhead = hasPedestrianObstacle || hasStoppedVehicleObstacle || hasSlowVehicleObstacle;

        // KHI PHÍA TRƯỚC CÓ VẬT CẢN MÀ BÊN CẠNH TRỐNG: ƯU TIÊN LÁCH!
        if (hasObstacleAhead)
        {
            if (!isChangingLane && laneChangeCooldownTimer <= 0f)
            {
                if (hasPedestrianObstacle)
                {
                    // 1. Chỉ chủ động né người qua đường khi phía trước và phía sau không có xe khác ở gần
                    if (canEvadePedestrian)
                    {
                        float pedWorldX = transform.position.x + nearestPedRelX;
                        bool chosenLaneFound = false;
                        int chosenTargetLane = currentLaneIndex;

                        // 2. KHI KHOẢNG TRỐNG TRƯỚC MẶT NGƯỜI QUA ĐƯỜNG ĐỦ RỘNG (>= 2.5m):
                        // Xe có thể tránh bằng cách đi qua TRƯỚC MẶT người qua đường!
                        if (nearestPedDir > 0 && isRightLaneSafeForPedEvade)
                        {
                            // Người đi sang phải (+X), trước mặt người là làn phải
                            float gapInFront = laneCenters[currentLaneIndex + 1] - pedWorldX;
                            if (gapInFront >= 2.5f)
                            {
                                chosenTargetLane = currentLaneIndex + 1;
                                chosenLaneFound = true;
                            }
                        }
                        else if (nearestPedDir < 0 && isLeftLaneSafeForPedEvade)
                        {
                            // Người đi sang trái (-X), trước mặt người là làn trái
                            float gapInFront = pedWorldX - laneCenters[currentLaneIndex - 1];
                            if (gapInFront >= 2.5f)
                            {
                                chosenTargetLane = currentLaneIndex - 1;
                                chosenLaneFound = true;
                            }
                        }

                        // 3. Nếu khoảng trống trước mặt không đủ rộng, kiểm tra lách qua SAU LƯNG người đi bộ
                        if (!chosenLaneFound)
                        {
                            if (nearestPedDir > 0 && isLeftLaneSafeForPedEvade)
                            {
                                // Người đi sang phải (+X), sau lưng là làn trái
                                float gapBehind = pedWorldX - laneCenters[currentLaneIndex - 1];
                                if (gapBehind >= 0.8f)
                                {
                                    chosenTargetLane = currentLaneIndex - 1;
                                    chosenLaneFound = true;
                                }
                            }
                            else if (nearestPedDir < 0 && isRightLaneSafeForPedEvade)
                            {
                                // Người đi sang trái (-X), sau lưng là làn phải
                                float gapBehind = laneCenters[currentLaneIndex + 1] - pedWorldX;
                                if (gapBehind >= 0.8f)
                                {
                                    chosenTargetLane = currentLaneIndex + 1;
                                    chosenLaneFound = true;
                                }
                            }
                        }

                        if (chosenLaneFound)
                        {
                            targetLaneIndex = chosenTargetLane;
                            targetLaneX = laneCenters[targetLaneIndex];
                            isChangingLane = true;
                            laneChangeCooldownTimer = 2.5f;
                        }
                        else
                        {
                            // Không có khoảng trống né an toàn: Giữ nguyên làn để dừng lại nhường đường
                            targetLaneIndex = currentLaneIndex;
                            targetLaneX = laneCenters[currentLaneIndex];
                        }
                    }
                    else
                    {
                        // NẾU CÓ XE KHÁC Ở GẦN (TRƯỚC HOẶC SAU): ƯU TIÊN DỪNG LẠI, KHÔNG CHỦ ĐỘNG LÁCH LÀN
                        targetLaneIndex = currentLaneIndex;
                        targetLaneX = laneCenters[currentLaneIndex];
                    }
                }
                else
                {
                    // Vật cản là xe trước dừng/chạy chậm: ƯU TIÊN LÁCH SANG LÀN TRỐNG NGAY
                    if (canGoLeft && canGoRight)
                    {
                        // Chọn làn có khoảng trống phía trước dài hơn để lách thoáng hơn
                        int chosenLane = (distLeftLaneAhead >= distRightLaneAhead) ? (currentLaneIndex - 1) : (currentLaneIndex + 1);
                        targetLaneIndex = chosenLane;
                        targetLaneX = laneCenters[targetLaneIndex];
                        isChangingLane = true;
                        laneChangeCooldownTimer = 2.5f;
                    }
                    else if (canGoLeft)
                    {
                        targetLaneIndex = currentLaneIndex - 1;
                        targetLaneX = laneCenters[targetLaneIndex];
                        isChangingLane = true;
                        laneChangeCooldownTimer = 2.5f;
                    }
                    else if (canGoRight)
                    {
                        targetLaneIndex = currentLaneIndex + 1;
                        targetLaneX = laneCenters[targetLaneIndex];
                        isChangingLane = true;
                        laneChangeCooldownTimer = 2.5f;
                    }
                }
            }
        }
        else
        {
            // KHI PHÍA TRƯỚC KHÔNG CÓ VẬT CẢN: KHÔNG ĐỔI HƯỚNG, GIỮ LÀN ĐI THẲNG
            if (!isChangingLane)
            {
                targetLaneIndex = currentLaneIndex;
                targetLaneX = laneCenters[currentLaneIndex];
                if (lightsController != null) lightsController.SetTurnSignal(TurnSignal.None);
            }
        }

        // Cập nhật trạng thái đang chuyển làn
        if (isChangingLane)
        {
            if (Mathf.Abs(currentPosX - targetLaneX) < 0.20f)
            {
                isChangingLane = false;
                currentLaneIndex = targetLaneIndex;
                if (lightsController != null) lightsController.SetTurnSignal(TurnSignal.None);
            }
            else
            {
                if (lightsController != null)
                {
                    if (targetLaneX < currentPosX) lightsController.SetTurnSignal(TurnSignal.Left);
                    else lightsController.SetTurnSignal(TurnSignal.Right);
                }
            }
        }

        // ===================================================================================
        // 3. ĐIỀU KHIỂN GA VÀ PHANH (IDM CAR-FOLLOWING VỚI KHOẢNG CÁCH GỌN GÀNG, TỰ NHIÊN):
        // Cự ly an toàn được tinh chỉnh vừa vặn (minGap = 2.2m, headwayTime = 0.55s), không bị cách xa quá mức
        // ===================================================================================
        float throttle = 0f;

        float minGap = 2.2f;
        float headwayTime = 0.55f / Mathf.Clamp(hurryFactor, 0.8f, 1.3f);
        float dynamicGap = 0f;
        if (closingSpeed > 0.1f)
        {
            dynamicGap = (currentSpeed * closingSpeed) / (2f * Mathf.Sqrt(maxAcceleration * 6.0f));
        }

        float desiredGap = minGap + (currentSpeed * headwayTime) + dynamicGap;
        if (isFrontCarBraking) desiredGap += 1.8f;
        if (frontCarTurnSignal != TurnSignal.None) desiredGap += 1.0f;

        // TRƯỜNG HỢP 1: CÓ NGƯỜI ĐI BỘ QUA ĐƯỜNG
        if (hasPedestrianObstacle)
        {
            if (isChangingLane)
            {
                // Khi đang lách né, duy trì tốc độ an toàn vừa phải
                float safeEvasionSpeed = Mathf.Min(effectiveTargetSpeed, 8.5f);
                throttle = (currentSpeed > safeEvasionSpeed) ? -0.2f : 0.3f;
                // Nếu cự ly tới người quá gần (< 3.0m), phanh hãm lại tránh va chạm
                if (nearestPedDist < 3.0f)
                {
                    throttle = -1.0f;
                    currentSpeed = Mathf.MoveTowards(currentSpeed, 0f, 15.0f * dt);
                }
            }
            else
            {
                // ƯU TIÊN DỪNG LẠI NHƯỜNG NGƯỜI ĐI BỘ (khi có xe gần hoặc không thể né an toàn)
                float safeStopGap = 4.5f + (currentSpeed * 0.7f);
                if (nearestPedDist > safeStopGap)
                {
                    float gapRatio = (nearestPedDist - safeStopGap) / safeStopGap;
                    throttle = Mathf.Clamp(gapRatio * 0.5f - 0.5f, -1.0f, 0.05f);
                }
                else
                {
                    throttle = -1.0f; // Phanh đứng lại nhường người đi bộ
                    if (lightsController != null) lightsController.SetBraking(true);
                    if (nearestPedDist < 4.0f)
                    {
                        currentSpeed = Mathf.MoveTowards(currentSpeed, 0f, 20.0f * dt);
                    }
                }
            }
        }
        // TRƯỜNG HỢP 2: XE TRƯỚC BỊ DỪNG HẲN / SỰ CỐ TĨNH (< 2.0 m/s)
        else if (hasStoppedVehicleObstacle)
        {
            if (isChangingLane)
            {
                if (distanceToFront < 6.5f)
                {
                    throttle = -0.9f;
                }
                else
                {
                    throttle = (currentSpeed > 7.0f) ? -0.2f : 0.35f;
                }
            }
            else
            {
                if (distanceToFront > 5.5f)
                {
                    float gapRatio = (distanceToFront - 4.5f) / Mathf.Max(2.0f, distanceToFront);
                    throttle = Mathf.Clamp(gapRatio * 0.6f - 0.7f, -1.0f, -0.1f);
                }
                else
                {
                    throttle = -1.0f; // Dừng lại an toàn
                }
            }
        }
        // TRƯỜNG HỢP 3: ĐƯỜNG PHÍA TRƯỚC THÔNG THOÁNG
        else if (isOpenRoadAhead)
        {
            if (currentSpeed < effectiveTargetSpeed * 0.98f)
            {
                float deficit = (effectiveTargetSpeed - currentSpeed) / effectiveTargetSpeed;
                throttle = Mathf.Clamp(deficit * 1.5f, 0.35f, 1.0f);
            }
            else if (currentSpeed > effectiveTargetSpeed * 1.05f)
            {
                throttle = 0f;
            }
            else
            {
                throttle = 0.12f;
            }
        }
        // TRƯỜNG HỢP 4: BÁM ĐUÔI XE ĐANG CHẠY BÌNH THƯỜNG (CAR-FOLLOWING)
        else
        {
            if (distanceToFront > desiredGap * 1.15f)
            {
                // Cự ly còn thoáng: Tăng ga tiến lại gần duy trì khoảng cách gọn gàng
                float gapRatio = (distanceToFront - desiredGap) / desiredGap;
                throttle = Mathf.Clamp(gapRatio * 0.8f, 0.20f, 0.90f);
            }
            else if (distanceToFront > desiredGap)
            {
                // Đang trong cự ly lý tưởng: Khớp nhịp nhàng tốc độ với xe trước
                float speedDiff = leaderSpeed - currentSpeed;
                throttle = Mathf.Clamp(speedDiff / 2.0f + 0.10f, -0.25f, 0.40f);
            }
            else
            {
                // Thu hẹp dưới desiredGap: Hãm nhẹ tỷ lệ thuận với cự ly thiếu hụt
                float deficit = (desiredGap - distanceToFront) / Mathf.Max(1.5f, desiredGap);
                throttle = Mathf.Clamp(-0.25f - deficit * 0.70f, -1.0f, -0.05f);
            }

            if (isFrontCarBraking && distanceToFront < desiredGap * 1.2f)
            {
                throttle = Mathf.Min(throttle, -0.75f);
            }
        }

        // HARD EMERGENCY OVERRIDE TRÁNH ĐÂM ĐUÔI XE TRƯỚC (CHỈ KÍCH HOẠT KHI DƯỚI 3.2M):
        if (frontVehicleObj != null && distanceToFront < 3.2f)
        {
            throttle = -1.0f;
            if (lightsController != null) lightsController.SetBraking(true);

            if (distanceToFront < 2.0f)
            {
                currentSpeed = Mathf.Min(currentSpeed, leaderSpeed);
                if (currentSpeed > 0.2f && leaderSpeed <= 0.1f)
                {
                    currentSpeed = Mathf.MoveTowards(currentSpeed, 0f, 25.0f * dt);
                }
            }
            else if (currentSpeed > leaderSpeed)
            {
                currentSpeed = Mathf.MoveTowards(currentSpeed, leaderSpeed, 18.0f * dt);
            }
        }

        // Cập nhật tốc độ dựa trên gia tốc
        float openRoadBoost = (isOpenRoadAhead && hurryFactor > 1.0f) ? (1.0f + (hurryFactor - 1.0f) * 0.7f) : 1.0f;
        float effectiveMaxAccel = maxAcceleration * Mathf.Sqrt(Mathf.Clamp(hurryFactor, 0.6f, 1.6f)) * openRoadBoost;
        float accel = throttle * effectiveMaxAccel;
        currentSpeed = Mathf.Clamp(currentSpeed + accel * dt, 0f, effectiveTargetSpeed * 1.15f);

        // ===================================================================================
        // 4. ĐIỀU KHIỂN ĐÁNH LÁI & XỬ LÝ 2 XE ĐI SONG SONG:
        // - KHI 2 XE ĐI SONG SONG NHƯNG KHÔNG GẦN NHAU (dist >= 0.50m): ƯU TIÊN ĐI THẲNG!
        // - KHI 2 XE ĐI SONG SONG SÁT NHAU (dist < 0.50m): ƯU TIÊN TÁCH XA NHAU RA!
        // - KHI ĐANG LÁCH VẬT CẢN: ĐÁNH LÁI MƯỢT MÀ VÀO LÀN TRỐNG
        // ===================================================================================
        float laneOffset = targetLaneX - currentPosX;
        float steering = 0f;
        float parallelSeparationVelX = 0f;

        // Xử lý né đẩy khi 2 xe đi song song sát nhau:
        float sideSeparationSteer = 0f;
        if (distToCarRight < 0.50f)
        {
            // Xe bên phải quá sát (< 50cm): Lực đẩy dạt sang TRÁI để tách xa ra
            float urgency = (0.50f - distToCarRight) / 0.50f;
            sideSeparationSteer = -urgency * 0.45f;
            parallelSeparationVelX = -urgency * 1.5f;
        }
        else if (distToCarLeft < 0.50f)
        {
            // Xe bên trái quá sát (< 50cm): Lực đẩy dạt sang PHẢI để tách xa ra
            float urgency = (0.50f - distToCarLeft) / 0.50f;
            sideSeparationSteer = urgency * 0.45f;
            parallelSeparationVelX = urgency * 1.5f;
        }

        // Không cho phép lực tách sườn đẩy xe đâm vào mép rào chắn
        if (currentPosX <= -maxSafeRoadX + 0.4f && parallelSeparationVelX < 0f) { parallelSeparationVelX = 0f; sideSeparationSteer = Mathf.Max(0f, sideSeparationSteer); }
        if (currentPosX >= maxSafeRoadX - 0.4f && parallelSeparationVelX > 0f) { parallelSeparationVelX = 0f; sideSeparationSteer = Mathf.Min(0f, sideSeparationSteer); }

        if (isChangingLane)
        {
            // Đang chủ động lách né vật cản sang làn trống
            steering = Mathf.Clamp(laneOffset * 0.80f + sideSeparationSteer, -0.65f, 0.65f);
        }
        else if (Mathf.Abs(sideSeparationSteer) > 0.01f)
        {
            // 2 xe đi song song SÁT NHAU: ƯU TIÊN TÁCH XA NHAU RA
            steering = Mathf.Clamp(sideSeparationSteer, -0.55f, 0.55f);
        }
        else
        {
            // 2 xe đi song song KHÔNG GẦN NHAU hoặc đường trống: ƯU TIÊN ĐI THẲNG
            if (Mathf.Abs(laneOffset) > 0.08f)
            {
                steering = Mathf.Clamp(laneOffset * 0.35f, -0.15f, 0.15f);
            }
            else
            {
                steering = 0f; // Khóa cứng đi thẳng
            }
        }

        // Kiểm tra an toàn lề đường (chỉ phản ứng khi xe thực sự đi sát mép rào)
        float distToLeftBarrier = transform.position.x - (-maxSafeRoadX);
        float distToRightBarrier = maxSafeRoadX - transform.position.x;
        float barrierSafetyMargin = 0.65f;
        float barrierRepulsion = 0f;
        if (distToLeftBarrier < barrierSafetyMargin)
        {
            barrierRepulsion += Mathf.Clamp01((barrierSafetyMargin - distToLeftBarrier) / barrierSafetyMargin);
        }
        if (distToRightBarrier < barrierSafetyMargin)
        {
            barrierRepulsion -= Mathf.Clamp01((barrierSafetyMargin - distToRightBarrier) / barrierSafetyMargin);
        }
        if (Mathf.Abs(barrierRepulsion) > 0.01f)
        {
            steering = Mathf.Clamp(steering + barrierRepulsion * 1.0f, -0.65f, 0.65f);
        }

        // Hệ thống tách va chạm (Separation & Recovery)
        if (vehicleCollisionTimer > 0f) vehicleCollisionTimer -= dt;
        else { isCollidingWithVehicle = false; vehicleSeparationVector = Vector3.zero; }

        if (barrierCollisionTimer > 0f) barrierCollisionTimer -= dt;
        else { isCollidingWithBarrier = false; barrierSeparationVector = Vector3.zero; }

        bool isNearCurb = Mathf.Abs(currentPosX) >= (maxSafeRoadX - 0.25f);
        if (isNearCurb)
        {
            float curbRepelDir = -Mathf.Sign(currentPosX);
            float curbPenetration = Mathf.Clamp01((Mathf.Abs(currentPosX) - (maxSafeRoadX - 0.25f)) / 0.25f);
            Vector3 proactiveCurbSep = new Vector3(curbRepelDir * curbPenetration, 0f, 0.2f);
            if (barrierSeparationVector == Vector3.zero) barrierSeparationVector = proactiveCurbSep;
            else barrierSeparationVector = (barrierSeparationVector + proactiveCurbSep).normalized;
        }

        Vector3 vehicleSeparation = Vector3.zero;
        Vector3 curbSeparation = Vector3.zero;

        if (isCollidingWithVehicle || vehicleCollisionTimer > 0f)
        {
            if (vehicleSeparationVector.z < -0.1f)
            {
                throttle = -1.0f;
                currentSpeed = 0f;
                vehicleSeparation.z += vehicleSeparationVector.z * (vehicleSeparationSpeed * dt);
                if (lightsController != null) lightsController.SetBraking(true);
            }
            else if (vehicleSeparationVector.z > 0.1f)
            {
                currentSpeed = Mathf.Max(currentSpeed, 3.0f);
                vehicleSeparation.z += vehicleSeparationVector.z * (vehicleSeparationSpeed * dt);
            }

            if (Mathf.Abs(vehicleSeparationVector.x) > 0.1f)
            {
                vehicleSeparation.x += vehicleSeparationVector.x * (vehicleSeparationSpeed * dt);
                steering = Mathf.Clamp(steering + Mathf.Sign(vehicleSeparationVector.x) * 0.6f, -1f, 1f);
            }
        }

        if (isCollidingWithBarrier || barrierCollisionTimer > 0f || isNearCurb)
        {
            float curbSide = Mathf.Sign(currentPosX);
            float awayFromCurbDir = -curbSide;
            float curbPenetration = Mathf.Clamp01((Mathf.Abs(currentPosX) - (maxSafeRoadX - 0.5f)) / 0.5f);
            float dynamicCurbSpeed = Mathf.Lerp(curbSeparationSpeed * 0.7f, curbSeparationSpeed * 1.2f, curbPenetration);

            curbSeparation.x = awayFromCurbDir * (dynamicCurbSpeed * dt);
            curbSeparation.z = 0.5f * dt;
            steering = awayFromCurbDir * Mathf.Lerp(0.5f, 0.9f, curbPenetration);
        }

        if (currentPosX < -3.5f && vehicleSeparation.x < 0f) vehicleSeparation.x = 0f;
        if (currentPosX > 3.5f && vehicleSeparation.x > 0f) vehicleSeparation.x = 0f;

        Vector3 totalSeparation = vehicleSeparation + curbSeparation;

        // Góc quay mong muốn: Khi không có vật cản và đang bám đúng làn, desiredYaw = 0f (ĐI THẲNG TUYỆT ĐỐI)
        float currentYaw = transform.eulerAngles.y;
        if (currentYaw > 180f) currentYaw -= 360f;

        float desiredYaw = 0f;
        if (isChangingLane || Mathf.Abs(laneOffset) > 0.08f || isCollidingWithVehicle || isCollidingWithBarrier)
        {
            desiredYaw = Mathf.Clamp(steering * maxHeadingAngle, -maxHeadingAngle, maxHeadingAngle);
        }

        float newYaw = Mathf.MoveTowardsAngle(currentYaw, desiredYaw, 45f * dt);
        if (desiredYaw == 0f && Mathf.Abs(newYaw) < 0.2f) newYaw = 0f;
        newYaw = Mathf.Clamp(newYaw, -maxHeadingAngle, maxHeadingAngle);

        Quaternion safeRot = Quaternion.Euler(0f, newYaw, 0f);
        transform.rotation = safeRot;
        if (rb != null)
        {
            rb.rotation = safeRot;
            rb.angularVelocity = Vector3.zero;
        }

        // Chuyển động vật lý
        Vector3 targetVelocity = transform.forward * currentSpeed;
        targetVelocity.y = 0f;

        if (Mathf.Abs(parallelSeparationVelX) > 0.01f)
        {
            // Bổ sung vận tốc đẩy tách ngang khi 2 xe đi song song quá sát nhau
            targetVelocity.x += parallelSeparationVelX;
        }
        else if (!isChangingLane && Mathf.Abs(laneOffset) <= 0.08f && !isCollidingWithVehicle && !isCollidingWithBarrier)
        {
            // Ưu tiên đi thẳng tuyệt đối: Triệt tiêu hoàn toàn vận tốc ngang
            targetVelocity.x = 0f;
            targetVelocity.z = currentSpeed;
        }

        if (dt > 0.0001f)
        {
            Vector3 sepVel = totalSeparation / dt;
            targetVelocity.x += Mathf.Clamp(sepVel.x, -curbSeparationSpeed, curbSeparationSpeed);
            targetVelocity.z += Mathf.Clamp(sepVel.z, -0.6f, 0.6f);
        }

        if (isCollidingWithVehicle && vehicleSeparationVector.z < -0.1f)
        {
            targetVelocity.z = Mathf.Min(0f, targetVelocity.z);
            currentSpeed = 0f;
        }
        else if (targetVelocity.z < 0f && !isCollidingWithVehicle)
        {
            targetVelocity.z = 0f;
        }

        if (rb.position.x >= maxSafeRoadX - 0.35f && targetVelocity.x > 0f)
        {
            targetVelocity.x = -curbSeparationSpeed * 0.6f;
        }
        else if (rb.position.x <= -maxSafeRoadX + 0.35f && targetVelocity.x < 0f)
        {
            targetVelocity.x = curbSeparationSpeed * 0.6f;
        }

        rb.linearVelocity = targetVelocity;

        // Giữ cao độ Y cố định và khống chế biên đường X mà không teleport xuyên qua xe khác
        Vector3 curPos = rb.position;
        curPos.y = fixedHeightY;
        if (curPos.x > maxSafeRoadX) curPos.x = maxSafeRoadX - 0.15f;
        else if (curPos.x < -maxSafeRoadX) curPos.x = -maxSafeRoadX + 0.15f;
        rb.position = curPos;

        // Nếu xe thực sự va chạm vào rào chắn mép đường (sau thời gian spawn an toàn): LẬP TỨC CHUYỂN SANG MÀU ĐỎ RỰC
        if (isCollidingWithBarrier && (Time.time - spawnTime >= 1.0f))
        {
            if (collisionVisualizer != null && !collisionVisualizer.IsInCrashState)
            {
                collisionVisualizer.TriggerCrash(3.0f);
            }
        }

        // 4. Cập nhật đèn đuôi xe (Phanh đỏ rực, Xi nhan vàng)
        if (lightsController != null)
        {
            // Bật đèn phanh đỏ khi ga âm, xe trước quá gần hoặc đang trong trạng thái va chạm
            bool isBrakingNow = (throttle < -0.05f) || (distanceToFront < 6f && currentSpeed > 0.5f) || (collisionVisualizer != null && collisionVisualizer.IsInCrashState);
            lightsController.SetBraking(isBrakingNow);

            // Bật đèn xi nhan vàng khi chuyển làn né vật cản, tuyệt đối không bật ngẫu nhiên khi đi thẳng
            if (isChangingLane)
            {
                if (targetLaneX < currentPosX) lightsController.SetTurnSignal(TurnSignal.Left);
                else if (targetLaneX > currentPosX) lightsController.SetTurnSignal(TurnSignal.Right);
            }
            else
            {
                lightsController.SetTurnSignal(TurnSignal.None);
            }
        }

        // Reset xe nếu đi hết đoạn đường (Loop simulation)
        if (transform.position.z >= 490f)
        {
            transform.position = new Vector3(targetLaneX, fixedHeightY, 5f);
            currentSpeed = 5f;
            rb.linearVelocity = transform.forward * 5f;
        }

        // In trạng thái hoạt động của xe số 0 lên Console định kỳ 1 giây/lần
        if (myId == 0 && Time.frameCount % 50 == 0)
        {
            string frontLightStatus = isFrontCarBraking ? "PHANH ĐỎ" : (frontCarTurnSignal != TurnSignal.None ? "XI NHAN VÀNG" : "Tắt");
            string roadStatus = isOpenRoadAhead ? "ĐƯỜNG TRỐNG (Tăng tốc)" : $"Gần xe trước ({distanceToFront:F1}m)";
            Debug.Log($"[CarAgent 0] Tốc độ: {currentSpeed:F1}/{effectiveTargetSpeed:F1} m/s (Độ vội: {hurryFactor:F2}) | Ga: {throttle:F2} | Vị trí X: {transform.position.x:F1}m (Mép: {normalizedRoadOffset:F2}) | {roadStatus} | Đèn xe trước: {frontLightStatus}");
        }
    }

    void LateUpdate()
    {
        // Kiểm tra sau khi PhysX chạy: nếu va chạm mạnh làm lệch góc quá giới hạn, lập tức nắn thẳng lại hướng an toàn
        float yaw = transform.eulerAngles.y;
        if (yaw > 180f) yaw -= 360f;

        // Nếu xe bị quay quá góc cố định hoặc chiều tiến trục Z bị âm (quay ngược):
        if (Mathf.Abs(yaw) > maxHeadingAngle || transform.forward.z <= 0.1f)
        {
            float clampedYaw = Mathf.Clamp(yaw, -maxHeadingAngle, maxHeadingAngle);
            if (transform.forward.z <= 0.1f)
            {
                clampedYaw = 0f; // Cưỡng bức hướng thẳng về phía trước (+Z), ngăn tuyệt đối việc đi ngược chiều
            }

            Quaternion safeRot = Quaternion.Euler(0f, clampedYaw, 0f);
            transform.rotation = safeRot;
            if (rb != null)
            {
                rb.rotation = safeRot;
                rb.angularVelocity = Vector3.zero;
            }
        }
    }

    private void OnCollisionEnter(Collision collision)
    {
        ProcessSeparationCollision(collision);
    }

    private void OnCollisionStay(Collision collision)
    {
        ProcessSeparationCollision(collision);
    }

    private void OnCollisionExit(Collision collision)
    {
        GameObject other = collision.gameObject;
        if (other.CompareTag("Vehicle") || other.GetComponentInParent<AIFVehicleAgent>() != null)
        {
            isCollidingWithVehicle = false;
            vehicleSeparationVector = Vector3.zero;
        }
        string otherName = other.name.ToLower();
        if (otherName.Contains("guardrail") || otherName.Contains("barrier") || otherName.Contains("wall") || otherName.Contains("curb"))
        {
            isCollidingWithBarrier = false;
            barrierSeparationVector = Vector3.zero;
        }
    }

    private void ProcessSeparationCollision(Collision collision)
    {
        // Tuyệt đối bỏ qua xử lý va chạm trong 1.0s đầu sau khi spawn để tránh lỗi settle vật lý
        if (Time.time - spawnTime < 1.0f) return;

        GameObject other = collision.gameObject;

        // Xử lý va chạm với người đi bộ: Tô đỏ CẢ XE VÀ NGƯỜI, CẢ HAI CÙNG DỪNG LẠI một vài giây
        if (other.CompareTag("Pedestrian") || other.GetComponentInParent<PedestrianAgent>() != null)
        {
            pedestrianCollisionPauseTimer = 3.5f;
            currentSpeed = 0f;
            if (rb != null) rb.linearVelocity = Vector3.zero;
            if (lightsController != null) lightsController.SetBraking(true);
            if (collisionVisualizer != null) collisionVisualizer.TriggerCrash(3.5f);

            // Tách hoàn toàn va chạm vật lý (PhysX contact) giữa xe và người để không bị kéo theo nhau
            IgnoreCollisionWithObject(other, 6.0f);

            var pedAgent = other.GetComponentInParent<PedestrianAgent>();
            if (pedAgent != null)
            {
                pedAgent.TriggerCrash(3.5f, gameObject);
                pedAgent.IgnoreCollisionWithVehicle(gameObject, 6.0f);
            }

            // Xe nhích nhẹ lùi lại 40cm theo Z và triệt tiêu toàn bộ vận tốc để tách rời Collider
            if (rb != null)
            {
                Vector3 p = rb.position;
                p.z -= 0.4f;
                rb.position = p;
                rb.linearVelocity = Vector3.zero;
            }
            return;
        }

        // 1. VA CHẠM GIỮA 2 XE -> CẢ HAI XE LẬP TỨC CHUYỂN ĐỎ & TÁCH NHAU RA VỪA ĐỦ
        if (other.CompareTag("Vehicle") || other.GetComponentInParent<AIFVehicleAgent>() != null || other.GetComponentInParent<CarCollisionVisualizer>() != null)
        {
            isCollidingWithVehicle = true;
            vehicleCollisionTimer = 0.2f;

            // KÍCH HOẠT MÀU ĐỎ CẢNH BÁO CHO CẢ HAI XE
            if (collisionVisualizer != null) collisionVisualizer.TriggerCrash(3.0f);
            var otherVisualizer = other.GetComponentInParent<CarCollisionVisualizer>();
            if (otherVisualizer != null) otherVisualizer.TriggerCrash(3.0f);

            AIFVehicleAgent otherAgent = other.GetComponentInParent<AIFVehicleAgent>();
            Vector3 diff = transform.position - other.transform.position;
            diff.y = 0f;

            float dx = diff.x;
            float dz = diff.z;

            // Nếu va chạm đầu - đuôi (Rear-end)
            if (dz < -0.8f)
            {
                // Xe này là XE SAU (đâm vào đuôi xe trước):
                // 1. Triệt tiêu hoàn toàn vận tốc tiến tới
                currentSpeed = 0f;
                if (lightsController != null) lightsController.SetBraking(true);

                // 2. Chỉ bật lùi ra vừa đủ (15cm) để không dính vào nhau, TUYỆT ĐỐI KHÔNG BẬT RA XA
                Vector3 p = rb.position;
                p.z -= 0.15f;
                rb.position = p;
                vehicleSeparationVector = new Vector3(0f, 0f, -0.4f);

                // Xe trước chỉ nhích nhẹ thoát kẹt (+2.0m/s), không bắn vọt đi xa
                if (otherAgent != null)
                {
                    otherAgent.currentSpeed = Mathf.Max(otherAgent.currentSpeed, 2.5f);
                    otherAgent.vehicleSeparationVector = new Vector3(0f, 0f, 0.4f);
                    otherAgent.vehicleCollisionTimer = 0.15f;
                }
            }
            else if (dz > 0.8f)
            {
                // Xe này là XE TRƯỚC (bị xe sau húc vào):
                currentSpeed = Mathf.Max(currentSpeed, 2.5f);
                vehicleSeparationVector = new Vector3(0f, 0f, 0.4f);
            }
            else
            {
                // Va chạm sườn nhẹ: nhích nhẹ sang 2 bên vừa đủ (12cm)
                float sideDir = (dx >= 0f) ? 1.0f : -1.0f;
                Vector3 p = rb.position;
                p.x += sideDir * 0.12f;
                rb.position = p;

                vehicleSeparationVector = new Vector3(sideDir * 0.6f, 0f, 0f);
            }

            return;
        }

        // 2. VA CHẠM VÀO LỀ ĐƯỜNG / RÀO CHẮN -> LẬP TỨC CHUYỂN ĐỎ & BẬT NHẸ VÀO LÒNG ĐƯỜNG
        string name = other.name.ToLower();
        // Bỏ qua mặt đường Plane Road (xe luôn tiếp xúc với mặt đường)
        if (name == "road" || name.StartsWith("road")) return;

        if (name.Contains("guardrail") || name.Contains("barrier") || name.Contains("wall") || name.Contains("curb"))
        {
            isCollidingWithBarrier = true;
            barrierCollisionTimer = 0.25f;

            // XE LẬP TỨC CHUYỂN SANG MÀU ĐỎ CẢNH BÁO
            if (collisionVisualizer != null) collisionVisualizer.TriggerCrash(3.0f);

            float posX = transform.position.x;
            float curbRepelDir = -Mathf.Sign(posX);

            // Bật nhẹ ra khỏi lề đường vừa đủ (15cm)
            Vector3 p = rb.position;
            p.x += curbRepelDir * 0.15f;
            rb.position = p;

            barrierSeparationVector = new Vector3(curbRepelDir * 1.5f, 0f, 0.3f);
        }
    }

    /// <summary>
    /// Nhận thông báo va chạm từ người đi bộ
    /// </summary>
    public void NotifyPedestrianCollision(GameObject pedObj)
    {
        pedestrianCollisionPauseTimer = 3.5f;
        currentSpeed = 0f;
        if (rb != null) rb.linearVelocity = Vector3.zero;
        if (lightsController != null) lightsController.SetBraking(true);
        if (collisionVisualizer != null) collisionVisualizer.TriggerCrash(3.5f);

        IgnoreCollisionWithObject(pedObj, 6.0f);

        if (rb != null)
        {
            Vector3 p = rb.position;
            p.z -= 0.4f;
            rb.position = p;
            rb.linearVelocity = Vector3.zero;
        }
    }

    /// <summary>
    /// Vô hiệu hóa va chạm vật lý giữa xe và đối tượng khác trong khoảng thời gian xác định
    /// </summary>
    public void IgnoreCollisionWithObject(GameObject otherObj, float duration)
    {
        if (otherObj == null) return;
        StartCoroutine(IgnoreCollisionRoutine(otherObj, duration));
    }

    private System.Collections.IEnumerator IgnoreCollisionRoutine(GameObject otherObj, float duration)
    {
        if (otherObj == null) yield break;
        Collider[] myCols = GetComponentsInChildren<Collider>();
        Collider[] otherCols = otherObj.GetComponentsInChildren<Collider>();

        foreach (var m in myCols)
        {
            foreach (var o in otherCols)
            {
                if (m != null && o != null) Physics.IgnoreCollision(m, o, true);
            }
        }

        yield return new WaitForSeconds(duration);

        if (otherObj != null)
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

    void OnDestroy()
    {
        worker?.Dispose();
        worker = null;
    }

    private void OnDrawGizmosSelected()
    {
        Vector3 cCenter = new Vector3(transform.position.x, fixedHeightY, transform.position.z);
        Vector3 fCenter = cCenter + transform.forward * 2.05f;

        // 9 tia thẳng cản trước (Xanh cyan)
        Gizmos.color = Color.cyan;
        float[] fOffsets = new float[] { 0f, -0.22f, 0.22f, -0.45f, 0.45f, -0.68f, 0.68f, -0.90f, 0.90f };
        for (int i = 0; i < fOffsets.Length; i++)
        {
            Vector3 o = fCenter + transform.right * fOffsets[i];
            Gizmos.DrawRay(o, transform.forward * 25f);
        }

        // 8 tia chéo đón đầu (Vàng)
        Gizmos.color = Color.yellow;
        float[] dAngles = new float[] { -60f, -45f, -30f, -15f, 15f, 30f, 45f, 60f };
        for (int d = 0; d < dAngles.Length; d++)
        {
            Vector3 dir = Quaternion.Euler(0f, dAngles[d], 0f) * transform.forward;
            Gizmos.DrawRay(fCenter, dir * 18f);
        }

        // 4 điểm quét sườn xe mỗi bên (Tím hồng)
        Gizmos.color = Color.magenta;
        float[] sOffsetsZ = new float[] { 1.8f, 0.6f, -0.6f, -1.8f };
        for (int s = 0; s < sOffsetsZ.Length; s++)
        {
            Vector3 lO = cCenter + transform.forward * sOffsetsZ[s] - transform.right * 0.95f;
            Vector3 rO = cCenter + transform.forward * sOffsetsZ[s] + transform.right * 0.95f;
            Gizmos.DrawRay(lO, -transform.right * sideScanDistance);
            Gizmos.DrawRay(rO, transform.right * sideScanDistance);
        }

        // Đường tim làn mục tiêu (Xanh lá)
        Gizmos.color = Color.green;
        Gizmos.DrawLine(new Vector3(targetLaneX, fixedHeightY, transform.position.z - 5f), new Vector3(targetLaneX, fixedHeightY, transform.position.z + 15f));
    }
}
