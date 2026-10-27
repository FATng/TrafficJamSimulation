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

        // 1. Thu thập trạng thái (Observation Vector)
        effectiveTargetSpeed = targetSpeed * Mathf.Lerp(1.0f, hurryFactor, 0.6f);
        float speedError = (effectiveTargetSpeed - currentSpeed) / Mathf.Max(1f, effectiveTargetSpeed);

        // Tính toán ranh giới đường (road boundary) chuẩn hóa: [-1 = mép trái, 0 = tim đường, +1 = mép phải]
        float halfRoadWidth = Mathf.Max(1f, roadWidth * 0.5f);
        normalizedRoadOffset = Mathf.Clamp(transform.position.x / halfRoadWidth, -1f, 1f);

        // Báo cáo vị trí cho hệ thống tính toán Mean Field
        if (TrafficFieldManager.Instance != null)
        {
            TrafficFieldManager.Instance.RegisterVehiclePosition(transform.position);
        }
        // -----------------------------------------------------------------------------------
        // 1. DÒ TIA PHÍA TRƯỚC VÀ DÒ TIA CHÉO HÌNH QUẠT (DIAGONAL FAN-OUT SCAN):
        // - 3 tia thẳng trước mũi xe: bao phủ bề rộng xe (1.8m) phát hiện xe liền trước và người.
        // - 6 tia chéo mở rộng (±15°, ±30°, ±45°) tầm xa 28m: quét đón đầu người đi bộ đang
        //   di chuyển ngang và nhận biết xe cộ ở các góc chéo lân cận để chuyển làn an toàn.
        // -----------------------------------------------------------------------------------
        float distanceToFront = 50f;
        isFrontCarBraking = false;
        frontCarTurnSignal = TurnSignal.None;
        GameObject frontVehicleObj = null;

        bool isPedestrianInPath = false;
        float nearestPedDist = 50f;
        float nearestPedRelX = 0f;
        int nearestPedDir = 0;

        bool hasCarOnLeftDiag = false;
        bool hasCarOnRightDiag = false;

        Vector3 carCenter = new Vector3(transform.position.x, fixedHeightY, transform.position.z);
        Vector3 baseRayOrigin = carCenter + transform.forward * 2.05f;

        // A. 7 tia thẳng trước mũi xe phủ kín bề rộng 1.8m (tránh lọt khe góc chéo đuôi xe trước)
        float[] rayOffsetsX = new float[] { 0f, -0.35f, 0.35f, -0.65f, 0.65f, -0.85f, 0.85f };
        for (int r = 0; r < rayOffsetsX.Length; r++)
        {
            Vector3 origin = baseRayOrigin + transform.right * rayOffsetsX[r];
            if (Physics.Raycast(origin, transform.forward, out RaycastHit hit, 55f))
            {
                if (hit.collider.gameObject != gameObject)
                {
                    bool isVehicle = hit.collider.CompareTag("Vehicle") || hit.collider.GetComponentInParent<AIFVehicleAgent>() != null;
                    bool isPed = hit.collider.CompareTag("Pedestrian") || hit.collider.GetComponentInParent<PedestrianAgent>() != null;

                    if (isVehicle)
                    {
                        if (hit.distance < distanceToFront)
                        {
                            distanceToFront = hit.distance;
                            frontVehicleObj = hit.collider.gameObject;
                        }
                    }
                    else if (isPed)
                    {
                        isPedestrianInPath = true;
                        if (hit.distance < nearestPedDist)
                        {
                            nearestPedDist = hit.distance;
                            nearestPedRelX = hit.point.x - transform.position.x;
                            var pedAgent = hit.collider.GetComponentInParent<PedestrianAgent>();
                            if (pedAgent != null) nearestPedDir = pedAgent.crossingDirection;
                        }
                    }
                }
            }
        }

        // B. 6 tia quét chéo góc đón đầu (±15°, ±30°, ±45°) tầm xa 28m
        float[] diagAngles = new float[] { -45f, -30f, -15f, 15f, 30f, 45f };
        for (int d = 0; d < diagAngles.Length; d++)
        {
            Vector3 diagDir = Quaternion.Euler(0f, diagAngles[d], 0f) * transform.forward;
            if (Physics.Raycast(baseRayOrigin, diagDir, out RaycastHit diagHit, 28f))
            {
                if (diagHit.collider.gameObject != gameObject)
                {
                    bool isPed = diagHit.collider.CompareTag("Pedestrian") || diagHit.collider.GetComponentInParent<PedestrianAgent>() != null;
                    bool isVehicle = diagHit.collider.CompareTag("Vehicle") || diagHit.collider.GetComponentInParent<AIFVehicleAgent>() != null;

                    if (isPed)
                    {
                        float pedDistZ = diagHit.point.z - transform.position.z;
                        float pedDistX = diagHit.point.x - transform.position.x;

                        if (pedDistZ > 0.5f && pedDistZ < 28f)
                        {
                            var pedAgent = diagHit.collider.GetComponentInParent<PedestrianAgent>();
                            int pedDir = (pedAgent != null) ? pedAgent.crossingDirection : (pedDistX > 0 ? -1 : 1);
                            float pedSpeed = (pedAgent != null) ? pedAgent.walkingSpeed : 1.4f;

                            // Dự đoán vị trí của người đi bộ khi xe đến cùng tọa độ Z
                            float timeToMeet = pedDistZ / Mathf.Max(2.0f, currentSpeed);
                            float predictedPedX = diagHit.point.x + (pedDir * pedSpeed * timeToMeet);
                            float predictedRelX = predictedPedX - transform.position.x;

                            // Nếu người đang cắt ngang hướng đi của xe hoặc đang nằm gần luồng xe
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
                    else if (isVehicle)
                    {
                        // Kiểm tra xe ở làn chéo lân cận để đảm bảo an toàn khi chuyển làn
                        if (diagHit.distance < 12.0f)
                        {
                            if (diagAngles[d] < 0f) hasCarOnLeftDiag = true;
                            else if (diagAngles[d] > 0f) hasCarOnRightDiag = true;
                        }
                    }
                }
            }
        }

        float leaderSpeed = 0f;
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
        }
        float closingSpeed = (frontVehicleObj != null) ? Mathf.Max(0f, currentSpeed - leaderSpeed) : 0f;

        isOpenRoadAhead = (distanceToFront >= 22f && !isPedestrianInPath);

        // -----------------------------------------------------------------------------------
        // 2. DÒ XE ĐI SONG SONG HAI BÊN SƯỜN & KIỂM TRA KHÔNG GIAN LÀN ĐƯỜNG TRỐNG
        // -----------------------------------------------------------------------------------
        bool hasCarOnLeft = false;
        bool hasCarOnRight = false;
        float distToCarLeft = 50f;
        float distToCarRight = 50f;

        float halfWidth = 0.92f;
        float[] sideOffsetsZ = new float[] { 1.2f, -0.6f };

        // Quét sườn trái (-transform.right)
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
        }

        // Quét sườn phải (+transform.right)
        for (int s = 0; s < sideOffsetsZ.Length; s++)
        {
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

        // Quét kiểm tra góc chéo phía trước (Diagonal Front-Left & Diagonal Front-Right)
        bool isLeftDiagClear = !hasCarOnLeftDiag;
        bool isRightDiagClear = !hasCarOnRightDiag;
        Vector3 diagLeftDir = (-transform.right * 0.707f + transform.forward * 0.707f).normalized;
        Vector3 diagRightDir = (transform.right * 0.707f + transform.forward * 0.707f).normalized;
        Vector3 frontCenterPoint = carCenter + transform.forward * 2.05f;

        if (Physics.Raycast(frontCenterPoint, diagLeftDir, out RaycastHit diagLHit, 6.0f))
        {
            if (diagLHit.collider.gameObject != gameObject && (diagLHit.collider.CompareTag("Vehicle") || diagLHit.collider.GetComponentInParent<AIFVehicleAgent>() != null))
            {
                isLeftDiagClear = false;
            }
        }

        if (Physics.Raycast(frontCenterPoint, diagRightDir, out RaycastHit diagRHit, 6.0f))
        {
            if (diagRHit.collider.gameObject != gameObject && (diagRHit.collider.CompareTag("Vehicle") || diagRHit.collider.GetComponentInParent<AIFVehicleAgent>() != null))
            {
                isRightDiagClear = false;
            }
        }

        float currentPosX = transform.position.x;
        // Kiểm tra an toàn làn bên cạnh: Không có xe sườn, không có xe chéo và cách mép rào đường an toàn
        bool canGoLeft = !hasCarOnLeft && isLeftDiagClear && (currentPosX > -2.7f);
        bool canGoRight = !hasCarOnRight && isRightDiagClear && (currentPosX < 2.7f);

        float sideEvasionSteer = 0f;

        // Né xe đi song song sát sườn
        if (hasCarOnRight && canGoLeft)
        {
            float urgency = Mathf.Clamp01((sideScanDistance - distToCarRight) / sideScanDistance);
            sideEvasionSteer = -Mathf.Lerp(0.28f, sideEvasionSteerStrength, urgency);
        }
        else if (hasCarOnLeft && canGoRight)
        {
            float urgency = Mathf.Clamp01((sideScanDistance - distToCarLeft) / sideScanDistance);
            sideEvasionSteer = Mathf.Lerp(0.28f, sideEvasionSteerStrength, urgency);
        }
        else if (hasCarOnLeft || hasCarOnRight)
        {
            if (hurryFactor >= 1.0f) effectiveTargetSpeed += 1.5f;
            else effectiveTargetSpeed = Mathf.Max(3f, effectiveTargetSpeed - 1.5f);
        }

        // -----------------------------------------------------------------------------------
        // 2b. BỘ NÃO CHỦ ĐỘNG NÉ VẬT CẢN & CHUYỂN LÀN THÔNG MINH (ACTIVE OBSTACLE & LANE EVASION):
        // Khi phía trước có vật cản (xe trước chạy chậm/dừng hoặc người qua đường) VÀ
        // ở bên cạnh không có vật cản (canGoLeft hoặc canGoRight):
        // Xe CHỦ ĐỘNG BẺ LÁI NÉ SANG LÀN ĐÓ để tiếp tục di chuyển, không bị dừng chờ tắc nghẽn!
        // -----------------------------------------------------------------------------------
        float obstacleEvasionSteer = 0f;
        bool isEvadingObstacle = false;

        bool hasFrontVehicleObstacle = (frontVehicleObj != null && distanceToFront < 20.0f);
        bool hasPedestrianObstacle = (isPedestrianInPath && nearestPedDist < 26.0f);
        bool hasObstacleAhead = hasFrontVehicleObstacle || hasPedestrianObstacle;

        if (hasObstacleAhead)
        {
            // Tình huống A: Vật cản là người đi bộ cắt ngang
            if (hasPedestrianObstacle)
            {
                // Ưu tiên né vòng qua phía sau lưng hướng đi của người
                if (nearestPedDir > 0) // Người đi sang phải
                {
                    if (canGoLeft) { obstacleEvasionSteer = -0.45f; isEvadingObstacle = true; }
                    else if (nearestPedRelX < -0.8f && canGoRight) { obstacleEvasionSteer = 0.45f; isEvadingObstacle = true; }
                }
                else if (nearestPedDir < 0) // Người đi sang trái
                {
                    if (canGoRight) { obstacleEvasionSteer = 0.45f; isEvadingObstacle = true; }
                    else if (nearestPedRelX > 0.8f && canGoLeft) { obstacleEvasionSteer = -0.45f; isEvadingObstacle = true; }
                }
                else
                {
                    if (nearestPedRelX >= 0f && canGoLeft) { obstacleEvasionSteer = -0.45f; isEvadingObstacle = true; }
                    else if (nearestPedRelX < 0f && canGoRight) { obstacleEvasionSteer = 0.45f; isEvadingObstacle = true; }
                }

                if (Mathf.Abs(nearestPedRelX) > 1.8f) isEvadingObstacle = true;
            }
            // Tình huống B: Vật cản là xe phía trước đi chậm/dừng
            else if (hasFrontVehicleObstacle)
            {
                // Nếu cả 2 bên đều trống: Ưu tiên vượt trái nếu không ở sát lề trái, ngược lại vượt phải
                if (canGoLeft && canGoRight)
                {
                    obstacleEvasionSteer = (currentPosX > -1.2f) ? -0.45f : 0.45f;
                    isEvadingObstacle = true;
                }
                else if (canGoLeft)
                {
                    obstacleEvasionSteer = -0.45f; // Chuyển làn né sang trái
                    isEvadingObstacle = true;
                }
                else if (canGoRight)
                {
                    obstacleEvasionSteer = 0.45f; // Chuyển làn né sang phải
                    isEvadingObstacle = true;
                }
            }
        }

        float steering = isEvadingObstacle && Mathf.Abs(obstacleEvasionSteer) > 0.01f ? obstacleEvasionSteer : sideEvasionSteer;
        float throttle = 0f;

        // -----------------------------------------------------------------------------------
        // 3. ĐIỀU KHIỂN GA VÀ PHANH (INTELLIGENT DRIVER MODEL + DYNAMIC CLOSING SPEED):
        // - Tính toán khoảng cách an toàn mong muốn IDM s*(v, Δv) gồm:
        //   + Khoảng cách tĩnh tối thiểu s0 = 4.5m
        //   + Khoảng cách theo thời gian phản xạ (Headway Time) = v * T
        //   + Khoảng cách động theo vận tốc tiếp cận (Closing Speed Term) = (v * Δv) / (2 * sqrt(a*b))
        // - Khi có xe phía trước: Bắt đầu hãm phanh mượt mà từ xa (35-40m) nếu đang lao nhanh tới
        // - Khi né chướng ngại vật: Bắt buộc hãm tốc nếu thân xe chưa tách làn hẳn, ngăn đâm đuôi
        // -----------------------------------------------------------------------------------
        float minGap = 4.5f;
        float headwayTime = 1.35f / Mathf.Clamp(hurryFactor, 0.8f, 1.25f);
        float dynamicGap = 0f;
        if (closingSpeed > 0.05f)
        {
            // Thành phần động lực học IDM: phụ thuộc trực tiếp vào vận tốc tiếp cận closing speed
            dynamicGap = (currentSpeed * closingSpeed) / (2f * Mathf.Sqrt(maxAcceleration * 4.0f));
        }

        float desiredGap = minGap + (currentSpeed * headwayTime) + dynamicGap;
        if (isFrontCarBraking) desiredGap += Mathf.Max(5.0f, currentSpeed * 0.6f);
        if (frontCarTurnSignal != TurnSignal.None) desiredGap += 2.0f;

        if (hasObstacleAhead)
        {
            if (isEvadingObstacle)
            {
                // Kiểm tra xem thân xe đã né dạt hẳn sang làn trống bên cạnh chưa
                float frontRelX = (frontVehicleObj != null) ? (frontVehicleObj.transform.position.x - transform.position.x) : 99f;
                bool isClearedLaterally = (frontVehicleObj == null) || (Mathf.Abs(frontRelX) > 1.35f);

                if (!isClearedLaterally && distanceToFront < desiredGap)
                {
                    // Đang bẻ lái nhưng thân xe vẫn còn nằm trực diện trên làn xe trước:
                    // BẮT BUỘC HÃM PHANH / GIẢM TỐC, TUYỆT ĐỐI KHÔNG TĂNG GA ĐÂM ĐUÔI!
                    if (distanceToFront < 6.5f || currentSpeed > leaderSpeed + 0.5f)
                    {
                        throttle = -0.9f;
                    }
                    else
                    {
                        throttle = Mathf.Clamp((leaderSpeed - currentSpeed) / 3.0f, -0.6f, 0.05f);
                    }
                }
                else
                {
                    // Đã tách làn thành công hoặc cự ly phía trước còn thoáng: Duy trì ga lướt vượt an toàn
                    float safeEvasionSpeed = Mathf.Min(effectiveTargetSpeed, 10.0f);
                    if (currentSpeed > safeEvasionSpeed)
                    {
                        throttle = -0.2f;
                    }
                    else
                    {
                        throttle = 0.35f;
                    }
                }

                // Tự động bật xi nhan cảnh báo về phía chuyển làn né
                if (lightsController != null)
                {
                    if (obstacleEvasionSteer < -0.05f) lightsController.SetTurnSignal(TurnSignal.Left);
                    else if (obstacleEvasionSteer > 0.05f) lightsController.SetTurnSignal(TurnSignal.Right);
                }
            }
            else
            {
                // Không có làn trống để né: Phanh an toàn dừng lại nhường đường
                float minObstacleDist = hasPedestrianObstacle ? nearestPedDist : distanceToFront;
                float safeStopGap = 4.5f + (currentSpeed * 0.8f);

                if (minObstacleDist > safeStopGap)
                {
                    float gapRatio = (minObstacleDist - safeStopGap) / safeStopGap;
                    throttle = Mathf.Clamp(gapRatio * 0.6f - 0.6f, -1.0f, 0.1f);
                }
                else
                {
                    throttle = -1.0f; // Phanh đứng lại an toàn
                }
            }
        }
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
        else
        {
            // Bám đuôi xe phía trước với khoảng cách IDM động
            if (distanceToFront > desiredGap * 1.3f)
            {
                float gapRatio = (distanceToFront - desiredGap) / desiredGap;
                throttle = Mathf.Clamp(gapRatio * 0.8f, 0.05f, 0.85f);
            }
            else if (distanceToFront > desiredGap)
            {
                // Khớp tốc độ với xe trước để duy trì cự ly ổn định
                if (currentSpeed > leaderSpeed + 0.3f)
                {
                    throttle = Mathf.Clamp((leaderSpeed - currentSpeed) / 3.5f, -0.5f, 0f);
                }
                else
                {
                    throttle = 0.15f;
                }
            }
            else
            {
                // Quá gần hoặc khoảng cách bị thu hẹp: Phanh tỷ lệ với khoảng cách thiếu hụt
                float gapDeficit = (desiredGap - distanceToFront) / Mathf.Max(2.0f, desiredGap);
                throttle = Mathf.Clamp(-0.4f - gapDeficit * 0.8f, -1.0f, -0.1f);
            }

            if (isFrontCarBraking && distanceToFront < desiredGap * 1.25f)
            {
                throttle = Mathf.Min(throttle, -0.8f);
            }
        }

        // HARD EMERGENCY REAR-END OVERRIDE (PHANH KHẨN CẤP CHỐNG ĐÂM ĐUÔI XE TRƯỚC)
        if (frontVehicleObj != null && distanceToFront < 7.5f)
        {
            throttle = -1.0f;
            if (lightsController != null) lightsController.SetBraking(true);

            if (distanceToFront < 4.8f)
            {
                // Cự ly cực cận: Triệt tiêu tốc độ dư thừa ngay lập tức
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

        // Lực né rào chắn an toàn: chỉ kích hoạt khi xe thực sự đi sát mép rào (|X| >= 3.35m)
        float distToLeftBarrier = transform.position.x - (-maxSafeRoadX);
        float distToRightBarrier = maxSafeRoadX - transform.position.x;
        float barrierSafetyMargin = 0.75f;

        float barrierRepulsion = 0f;
        if (distToLeftBarrier < barrierSafetyMargin)
        {
            barrierRepulsion += Mathf.Clamp01((barrierSafetyMargin - distToLeftBarrier) / barrierSafetyMargin);
        }
        if (distToRightBarrier < barrierSafetyMargin)
        {
            barrierRepulsion -= Mathf.Clamp01((barrierSafetyMargin - distToRightBarrier) / barrierSafetyMargin);
        }

        if (!isOpenRoadAhead && Mathf.Abs(barrierRepulsion) > 0.01f)
        {
            steering = Mathf.Clamp(steering + barrierRepulsion * 1.2f, -1f, 1f);
        }

        // -----------------------------------------------------------------------------------
        // 3. HỆ THỐNG ƯU TIÊN TÁCH XE & TÁCH LỀ ĐƯỜNG (SEPARATION & RECOVERY SYSTEM)
        // - Với các xe đang đâm vào nhau: Ưu tiên tách 2 xe ra trước
        // - Với các xe va vào lề đường: Ưu tiên tách ra khỏi lề đường
        // -----------------------------------------------------------------------------------
        if (vehicleCollisionTimer > 0f) vehicleCollisionTimer -= dt;
        else { isCollidingWithVehicle = false; vehicleSeparationVector = Vector3.zero; }

        if (barrierCollisionTimer > 0f) barrierCollisionTimer -= dt;
        else { isCollidingWithBarrier = false; barrierSeparationVector = Vector3.zero; }

        // Kiểm tra chủ động khoảng cách với lề đường (chỉ kích hoạt khi xe trượt sát mép rào |X| >= 3.85m)
        currentPosX = transform.position.x;
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

        // A. Ưu tiên tách 2 xe đâm vào nhau:
        if (isCollidingWithVehicle || vehicleCollisionTimer > 0f)
        {
            // Tách theo phương dọc (Z)
            if (vehicleSeparationVector.z < -0.1f)
            {
                // Xe sau phanh khẩn cấp và triệt tiêu tốc độ tiến tới để KHÔNG đâm xuyên xe trước
                throttle = -1.0f;
                currentSpeed = 0f;
                vehicleSeparation.z += vehicleSeparationVector.z * (vehicleSeparationSpeed * dt);
                if (lightsController != null) lightsController.SetBraking(true);
            }
            else if (vehicleSeparationVector.z > 0.1f)
            {
                // Xe trước nhích nhẹ nhàng thoát hiểm (+2.5m/s), không phóng vọt đi xa
                currentSpeed = Mathf.Max(currentSpeed, 3.0f);
                vehicleSeparation.z += vehicleSeparationVector.z * (vehicleSeparationSpeed * dt);
            }

            // Tách theo phương ngang (X)
            if (Mathf.Abs(vehicleSeparationVector.x) > 0.1f)
            {
                vehicleSeparation.x += vehicleSeparationVector.x * (vehicleSeparationSpeed * dt);
                steering = Mathf.Clamp(steering + Mathf.Sign(vehicleSeparationVector.x) * 0.6f, -1f, 1f);
            }
        }

        // B. Ưu tiên tách ra khỏi lề đường:
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

        // C. Kiểm soát an toàn tương hỗ: Xe tách khỏi xe khác KHÔNG ĐƯỢC BỊ ĐẨY ĐÂM VÀO LỀ ĐƯỜNG
        if (currentPosX < -3.5f && vehicleSeparation.x < 0f) vehicleSeparation.x = 0f;
        if (currentPosX > 3.5f && vehicleSeparation.x > 0f) vehicleSeparation.x = 0f;

        Vector3 totalSeparation = vehicleSeparation + curbSeparation;

        // Khống chế góc quay yaw: luôn giữ xe tiến thẳng theo trục Z (+Z) khi đường trống và không né sườn
        float currentYaw = transform.eulerAngles.y;
        if (currentYaw > 180f) currentYaw -= 360f;

        bool isEvadingSide = Mathf.Abs(sideEvasionSteer) > 0.05f;

        // Góc quay mong muốn: khi đường trống phía trước và không cần né xe sườn, KHÓA CỨNG = 0 ĐỘ (ĐI THẲNG TUYỆT ĐỐI)
        float desiredYaw = (isOpenRoadAhead && !isEvadingSide && !isCollidingWithVehicle && !isCollidingWithBarrier && !isNearCurb)
            ? 0f
            : Mathf.Clamp(steering * maxHeadingAngle, -maxHeadingAngle, maxHeadingAngle);

        float newYaw = Mathf.MoveTowardsAngle(currentYaw, desiredYaw, 45f * dt);
        newYaw = Mathf.Clamp(newYaw, -maxHeadingAngle, maxHeadingAngle);

        Quaternion safeRot = Quaternion.Euler(0f, newYaw, 0f);
        transform.rotation = safeRot;
        if (rb != null)
        {
            rb.rotation = safeRot;
            rb.angularVelocity = Vector3.zero;
        }

        // -----------------------------------------------------------------------------------
        // 4. CHUYỂN ĐỘNG VẬT LÝ VẬT THỂ CỨNG (RIGIDBODY PHYSICAL VELOCITY):
        // Tuyệt đối KHÔNG dùng MovePosition cưỡng bức teleport xe vào nhau gây hiện tượng đi xuyên qua nhau.
        // Điều khiển hoàn toàn qua Rigidbody linearVelocity với ContinuousDynamic collision detection.
        // -----------------------------------------------------------------------------------
        Vector3 targetVelocity = transform.forward * currentSpeed;
        targetVelocity.y = 0f;

        // Tăng cường độ dịch chuyển ngang khi chủ động chuyển làn né vật cản
        if (isEvadingObstacle && Mathf.Abs(obstacleEvasionSteer) > 0.05f)
        {
            targetVelocity.x += obstacleEvasionSteer * 2.2f;
        }

        if (dt > 0.0001f)
        {
            Vector3 sepVel = totalSeparation / dt;
            targetVelocity.x += Mathf.Clamp(sepVel.x, -curbSeparationSpeed, curbSeparationSpeed);
            // Chỉ bù vận tốc tách vừa đủ (tối đa 0.6 m/s) để tránh bị bật nảy giật bắn ra xa
            targetVelocity.z += Mathf.Clamp(sepVel.z, -0.6f, 0.6f);
        }

        // Nếu xe sau đang va chạm đuôi xe trước: triệt tiêu hoàn toàn vận tốc tiến tới (+Z)
        if (isCollidingWithVehicle && vehicleSeparationVector.z < -0.1f)
        {
            targetVelocity.z = Mathf.Min(0f, targetVelocity.z);
            currentSpeed = 0f;
        }
        else if (targetVelocity.z < 0f && !isCollidingWithVehicle)
        {
            targetVelocity.z = 0f;
        }

        // Triệt tiêu vận tốc hướng ra lề đường khi ở sát mép rào chắn
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

            // Bật đèn xi nhan vàng khi bẻ lái
            if (lightsController.autoSignalFromSteering)
            {
                if (steering < -0.05f)
                {
                    lightsController.SetTurnSignal(TurnSignal.Left);
                }
                else if (steering > 0.05f)
                {
                    lightsController.SetTurnSignal(TurnSignal.Right);
                }
                else
                {
                    // Thỉnh thoảng mô phỏng xi nhan chuyển làn
                    demoSignalTimer += dt;
                    if (demoSignalTimer >= nextDemoSignalTime)
                    {
                        if (lightsController.CurrentSignal == TurnSignal.None)
                        {
                            TurnSignal demoSig = (Random.value > 0.5f) ? TurnSignal.Left : TurnSignal.Right;
                            lightsController.SetTurnSignal(demoSig);
                            nextDemoSignalTime = demoSignalTimer + Random.Range(2.5f, 4.0f);
                        }
                        else
                        {
                            lightsController.SetTurnSignal(TurnSignal.None);
                            nextDemoSignalTime = demoSignalTimer + Random.Range(8.0f, 16.0f);
                        }
                    }
                }
            }
        }

        // Reset xe nếu đi hết đoạn đường (Loop simulation)
        if (transform.position.z >= 490f)
        {
            transform.position = new Vector3(transform.position.x, 0.5f, 5f);
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
}
