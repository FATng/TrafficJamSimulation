using UnityEngine;
using Unity.InferenceEngine;
using TrafficJamSimulation;
using Random = UnityEngine.Random;

public enum DrivingMode
{
    FreeFlow,
    CarFollowing,
    ActiveEvasion,
    SafeYielding,
    EmergencyShield
}

public class AIFVehicleAgent : MonoBehaviour
{
    [SerializeField] private ModelAsset onnxModelAsset;
    [SerializeField] private float targetSpeed = 15f;
    [SerializeField] private float maxAcceleration = 8f;

    [Header("Hierarchical Architecture (Kiến trúc phân tầng)")]
    [SerializeField] private DrivingMode currentDrivingMode = DrivingMode.FreeFlow;
    public DrivingMode CurrentDrivingMode => currentDrivingMode;
    [SerializeField] private float aiSteeringOutput = 0f;
    [SerializeField] private float aiAccelerationOutput = 0f;
    private float smoothedSteering = 0f;
    private float smoothedAcceleration = 0f;

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
    [SerializeField] public float currentSpeed = 0f;
    public float CurrentSpeed => currentSpeed;
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
    private float nextDemoSignalTime = 5f;
    private float spawnTime = 0f;

    void Start()
    {
        spawnTime = Time.time;
        myId = instanceCounter++;
        rb = GetComponent<Rigidbody>();
        if (rb != null)
        {
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

        if (Mathf.Approximately(hurryFactor, 1.0f))
        {
            hurryFactor = Random.Range(0.75f, 1.35f);
        }
        effectiveTargetSpeed = targetSpeed * Mathf.Lerp(1.0f, hurryFactor, 0.6f);

        float spawnX = transform.position.x;
        if (spawnX < -1.2f) currentLaneIndex = 0;
        else if (spawnX > 1.2f) currentLaneIndex = 2;
        else currentLaneIndex = 1;

        targetLaneIndex = currentLaneIndex;
        if (laneCenters == null || laneCenters.Length == 0)
        {
            laneCenters = new float[] { -2.4f, 0f, 2.4f };
        }
        targetLaneX = transform.position.x;
        laneChangeCooldownTimer = Random.Range(2.0f, 5.0f);

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

        currentSpeed = 5f;

#if UNITY_EDITOR
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

    public (DrivingMode mode, float accel, float steer) PredictAIPolicy(
        float speed,
        float gap,
        float relSpeed,
        float targetSpd,
        float roadOffset,
        float distLeft,
        float distRight,
        bool isLeaderBraking,
        bool isPedAhead)
    {
        if (worker == null)
        {
            float fallbackAccel = (gap < 4.0f) ? -4.0f : ((speed < targetSpd) ? 1.5f : 0f);
            return (DrivingMode.FreeFlow, fallbackAccel, 0f);
        }

        float normSpeed = Mathf.Clamp01(speed / 30f);
        float normGap = Mathf.Clamp01(gap / 50f);
        float normRelSpeed = Mathf.Clamp(relSpeed / 15f, -1f, 1f);
        float speedError = Mathf.Clamp((targetSpd - speed) / Mathf.Max(1f, targetSpd), -1f, 1f);
        float normOffset = Mathf.Clamp(roadOffset, -1f, 1f);
        float sideBalance = Mathf.Clamp((distRight - distLeft) / 5f, -1f, 1f);
        float leaderBrakeInput = isLeaderBraking ? 1.0f : 0.0f;
        float pedInput = isPedAhead ? 1.0f : 0.0f;
        float trafficDensity = (TrafficFieldManager.Instance != null) ? TrafficFieldManager.Instance.GetDensityAt(transform.position) : 0f;

        float[] rawInputs = new float[] {
            normSpeed, normGap, normRelSpeed, speedError, normOffset, sideBalance, leaderBrakeInput, pedInput, trafficDensity
        };
        using var inputTensor = new Tensor<float>(new TensorShape(1, 9), rawInputs);

        try
        {
            worker.Schedule(inputTensor);
            var outputTensor = worker.PeekOutput() as Tensor<float>;
            if (outputTensor != null)
            {
                float[] results = outputTensor.DownloadToArray();
                if (results != null && results.Length >= 7)
                {
                    int bestModeIdx = 0;
                    float maxLogit = results[0];
                    for (int i = 1; i < 5; i++)
                    {
                        if (results[i] > maxLogit)
                        {
                            maxLogit = results[i];
                            bestModeIdx = i;
                        }
                    }
                    DrivingMode predictedMode = (DrivingMode)bestModeIdx;

                    float accel = Mathf.Clamp(results[5] * maxAcceleration, -8.0f, 4.0f);
                    float steer = Mathf.Clamp(results[6] * 0.45f, -0.45f, 0.45f);
                    return (predictedMode, accel, steer);
                }
                else if (results != null && results.Length >= 2)
                {
                    float accel = Mathf.Clamp(results[0] * maxAcceleration, -8.0f, 4.0f);
                    float steer = Mathf.Clamp(results[1] * 0.45f, -0.45f, 0.45f);
                    return (DrivingMode.FreeFlow, accel, steer);
                }
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[AIFVehicleAgent] Lỗi suy luận AI Sentis: {ex.Message}");
        }

        return (DrivingMode.FreeFlow, 0f, 0f);
    }

    public float PredictAIAcceleration(float speed, float gap, float relSpeed, float targetSpd, float roadOffset, float distLeft, float distRight)
    {
        return PredictAIPolicy(speed, gap, relSpeed, targetSpd, roadOffset, distLeft, distRight, false, false).accel;
    }

    void FixedUpdate()
    {
        if (rb == null) return;

        float dt = Time.fixedDeltaTime;

        if (pedestrianCollisionPauseTimer > 0f)
        {
            pedestrianCollisionPauseTimer -= dt;
            currentSpeed = 0f;
            if (rb != null) rb.linearVelocity = Vector3.zero;
            if (lightsController != null) lightsController.SetBraking(true);
            if (pedestrianCollisionPauseTimer <= 0f)
            {
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

        effectiveTargetSpeed = targetSpeed * Mathf.Lerp(1.0f, hurryFactor, 0.6f);
        float speedError = (effectiveTargetSpeed - currentSpeed) / Mathf.Max(1f, effectiveTargetSpeed);

        float halfRoadWidth = Mathf.Max(1f, roadWidth * 0.5f);
        normalizedRoadOffset = Mathf.Clamp(transform.position.x / halfRoadWidth, -1f, 1f);

        if (TrafficFieldManager.Instance != null)
        {
            TrafficFieldManager.Instance.RegisterVehiclePosition(transform.position);
        }

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
                    if (hitL.distance < distToCarLeft) distToCarLeft = hitL.distance;
                }
            }

            Vector3 rightOrigin = carCenter + transform.forward * sideOffsetsZ[s] + transform.right * halfWidth;
            if (Physics.Raycast(rightOrigin, transform.right, out RaycastHit hitR, sideScanDistance))
            {
                if (hitR.collider.gameObject != gameObject && (hitR.collider.CompareTag("Vehicle") || hitR.collider.GetComponentInParent<AIFVehicleAgent>() != null))
                {
                    if (hitR.distance < distToCarRight) distToCarRight = hitR.distance;
                }
            }
        }

        float currentPosX = transform.position.x;
        float lateralCorridorOffset = 2.0f;
        float leftCorridorX = currentPosX - lateralCorridorOffset;
        float rightCorridorX = currentPosX + lateralCorridorOffset;

        bool isLeftCorridorInBounds = (leftCorridorX >= -maxSafeRoadX + 0.35f);
        bool isRightCorridorInBounds = (rightCorridorX <= maxSafeRoadX - 0.35f);

        float distLeftLaneAhead = 50f;
        float distLeftLaneBehind = 50f;
        float distRightLaneAhead = 50f;
        float distRightLaneBehind = 50f;

        if (isLeftCorridorInBounds)
        {
            Vector3 leftCorridorOrigin = new Vector3(leftCorridorX, fixedHeightY, transform.position.z);
            if (Physics.Raycast(leftCorridorOrigin, Vector3.forward, out RaycastHit hitLAhead, 35f))
            {
                if (hitLAhead.collider.gameObject != gameObject && (hitLAhead.collider.CompareTag("Vehicle") || hitLAhead.collider.GetComponentInParent<AIFVehicleAgent>() != null))
                {
                    distLeftLaneAhead = hitLAhead.distance;
                }
            }
            if (Physics.Raycast(leftCorridorOrigin, Vector3.back, out RaycastHit hitLBehind, 22f))
            {
                if (hitLBehind.collider.gameObject != gameObject && (hitLBehind.collider.CompareTag("Vehicle") || hitLBehind.collider.GetComponentInParent<AIFVehicleAgent>() != null))
                {
                    distLeftLaneBehind = hitLBehind.distance;
                }
            }
        }

        if (isRightCorridorInBounds)
        {
            Vector3 rightCorridorOrigin = new Vector3(rightCorridorX, fixedHeightY, transform.position.z);
            if (Physics.Raycast(rightCorridorOrigin, Vector3.forward, out RaycastHit hitRAhead, 35f))
            {
                if (hitRAhead.collider.gameObject != gameObject && (hitRAhead.collider.CompareTag("Vehicle") || hitRAhead.collider.GetComponentInParent<AIFVehicleAgent>() != null))
                {
                    distRightLaneAhead = hitRAhead.distance;
                }
            }
            if (Physics.Raycast(rightCorridorOrigin, Vector3.back, out RaycastHit hitRBehind, 22f))
            {
                if (hitRBehind.collider.gameObject != gameObject && (hitRBehind.collider.CompareTag("Vehicle") || hitRBehind.collider.GetComponentInParent<AIFVehicleAgent>() != null))
                {
                    distRightLaneBehind = hitRBehind.distance;
                }
            }
        }

        float distOwnLaneBehind = 50f;
        Vector3 ownRearOrigin = carCenter - transform.forward * 2.0f;
        if (Physics.Raycast(ownRearOrigin, -transform.forward, out RaycastHit hitOwnBehind, 25f))
        {
            if (hitOwnBehind.collider.gameObject != gameObject && (hitOwnBehind.collider.CompareTag("Vehicle") || hitOwnBehind.collider.GetComponentInParent<AIFVehicleAgent>() != null))
            {
                distOwnLaneBehind = hitOwnBehind.distance;
            }
        }

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

        isOpenRoadAhead = (distanceToFront >= 16f && !isPedestrianInPath);

        bool canGoLeft = isLeftCorridorInBounds &&
                         (distToCarLeft >= 0.50f) &&
                         !hasCarOnLeftFrontDiag &&
                         (distLeftLaneAhead > 7.0f) &&
                         (distLeftLaneBehind > 5.0f);

        bool canGoRight = isRightCorridorInBounds &&
                          (distToCarRight >= 0.50f) &&
                          !hasCarOnRightFrontDiag &&
                          (distRightLaneAhead > 7.0f) &&
                          (distRightLaneBehind > 5.0f);

        bool isOwnLaneIsolated = (frontVehicleObj == null || distanceToFront >= 14.0f) && (distOwnLaneBehind >= 10.0f);

        bool isLeftLaneSafeForPedEvade = isLeftCorridorInBounds &&
                                        (distToCarLeft >= 1.0f) &&
                                        !hasCarOnLeftFrontDiag &&
                                        (distLeftLaneAhead >= 14.0f) &&
                                        (distLeftLaneBehind >= 10.0f);

        bool isRightLaneSafeForPedEvade = isRightCorridorInBounds &&
                                         (distToCarRight >= 1.0f) &&
                                         !hasCarOnRightFrontDiag &&
                                         (distRightLaneAhead >= 14.0f) &&
                                         (distRightLaneBehind >= 10.0f);

        bool canEvadePedestrian = isOwnLaneIsolated && (isLeftLaneSafeForPedEvade || isRightLaneSafeForPedEvade);

        bool hasPedestrianObstacle = (isPedestrianInPath && nearestPedDist < 26.0f);
        bool hasStoppedVehicleObstacle = (frontVehicleObj != null && isLeaderStopped && distanceToFront < 18.0f);
        bool hasSlowVehicleObstacle = (frontVehicleObj != null && !isLeaderStopped && distanceToFront < 14.0f && leaderSpeed < effectiveTargetSpeed * 0.70f);
        bool hasObstacleAhead = hasPedestrianObstacle || hasStoppedVehicleObstacle || hasSlowVehicleObstacle;

        if (hasObstacleAhead)
        {
            if (!isChangingLane && laneChangeCooldownTimer <= 0f)
            {
                if (hasPedestrianObstacle)
                {
                    if (canEvadePedestrian)
                    {
                        float pedWorldX = transform.position.x + nearestPedRelX;
                        bool chosenShiftFound = false;
                        float chosenTargetX = currentPosX;

                        if (nearestPedDir > 0 && isRightLaneSafeForPedEvade)
                        {
                            float targetCandidateX = Mathf.Min(currentPosX + lateralCorridorOffset, maxSafeRoadX - 0.4f);
                            float gapInFront = targetCandidateX - pedWorldX;
                            if (gapInFront >= 2.5f)
                            {
                                chosenTargetX = targetCandidateX;
                                chosenShiftFound = true;
                            }
                        }
                        else if (nearestPedDir < 0 && isLeftLaneSafeForPedEvade)
                        {
                            float targetCandidateX = Mathf.Max(currentPosX - lateralCorridorOffset, -maxSafeRoadX + 0.4f);
                            float gapInFront = pedWorldX - targetCandidateX;
                            if (gapInFront >= 2.5f)
                            {
                                chosenTargetX = targetCandidateX;
                                chosenShiftFound = true;
                            }
                        }

                        if (!chosenShiftFound)
                        {
                            if (nearestPedDir > 0 && isLeftLaneSafeForPedEvade)
                            {
                                float targetCandidateX = Mathf.Max(currentPosX - lateralCorridorOffset, -maxSafeRoadX + 0.4f);
                                float gapBehind = pedWorldX - targetCandidateX;
                                if (gapBehind >= 0.8f)
                                {
                                    chosenTargetX = targetCandidateX;
                                    chosenShiftFound = true;
                                }
                            }
                            else if (nearestPedDir < 0 && isRightLaneSafeForPedEvade)
                            {
                                float targetCandidateX = Mathf.Min(currentPosX + lateralCorridorOffset, maxSafeRoadX - 0.4f);
                                float gapBehind = targetCandidateX - pedWorldX;
                                if (gapBehind >= 0.8f)
                                {
                                    chosenTargetX = targetCandidateX;
                                    chosenShiftFound = true;
                                }
                            }
                        }

                        if (chosenShiftFound)
                        {
                            targetLaneX = chosenTargetX;
                            isChangingLane = true;
                            laneChangeCooldownTimer = 2.0f;
                        }
                        else
                        {
                            targetLaneX = currentPosX;
                        }
                    }
                    else
                    {
                        targetLaneX = currentPosX;
                    }
                }
                else
                {
                    if (canGoLeft && canGoRight)
                    {
                        float chosenShift = (distLeftLaneAhead >= distRightLaneAhead) ? -lateralCorridorOffset : lateralCorridorOffset;
                        targetLaneX = Mathf.Clamp(currentPosX + chosenShift, -maxSafeRoadX + 0.4f, maxSafeRoadX - 0.4f);
                        isChangingLane = true;
                        laneChangeCooldownTimer = 2.0f;
                    }
                    else if (canGoLeft)
                    {
                        targetLaneX = Mathf.Clamp(currentPosX - lateralCorridorOffset, -maxSafeRoadX + 0.4f, maxSafeRoadX - 0.4f);
                        isChangingLane = true;
                        laneChangeCooldownTimer = 2.0f;
                    }
                    else if (canGoRight)
                    {
                        targetLaneX = Mathf.Clamp(currentPosX + lateralCorridorOffset, -maxSafeRoadX + 0.4f, maxSafeRoadX - 0.4f);
                        isChangingLane = true;
                        laneChangeCooldownTimer = 2.0f;
                    }
                }
            }
        }
        else
        {
            if (!isChangingLane)
            {
                targetLaneX = currentPosX;
                if (lightsController != null) lightsController.SetTurnSignal(TurnSignal.None);
            }
        }

        if (isChangingLane)
        {
            if (Mathf.Abs(currentPosX - targetLaneX) < 0.25f)
            {
                isChangingLane = false;
                targetLaneX = currentPosX;
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

        float effectiveGap = 50f;
        float relSpeed = 0f;

        if (hasPedestrianObstacle)
        {
            effectiveGap = nearestPedDist;
            relSpeed = -currentSpeed;
        }
        else if (frontVehicleObj != null)
        {
            effectiveGap = distanceToFront;
            relSpeed = leaderSpeed - currentSpeed;
        }

        var (aiSelectedMode, predictedAccel, predictedSteer) = PredictAIPolicy(
            currentSpeed,
            effectiveGap,
            relSpeed,
            effectiveTargetSpeed,
            normalizedRoadOffset,
            distToCarLeft,
            distToCarRight,
            isFrontCarBraking,
            hasPedestrianObstacle
        );

        currentDrivingMode = aiSelectedMode;

        if (frontVehicleObj != null && distanceToFront < 3.2f)
        {
            currentDrivingMode = DrivingMode.EmergencyShield;
        }
        else if (hasPedestrianObstacle && !isChangingLane && nearestPedDist < 4.0f)
        {
            currentDrivingMode = DrivingMode.SafeYielding;
        }

        aiAccelerationOutput = predictedAccel;
        aiSteeringOutput = predictedSteer;

        float aiPredictedAccel = predictedAccel;

        float openRoadBoost = (isOpenRoadAhead && hurryFactor > 1.0f) ? (1.0f + (hurryFactor - 1.0f) * 0.7f) : 1.0f;
        float effectiveMaxAccel = maxAcceleration * Mathf.Sqrt(Mathf.Clamp(hurryFactor, 0.6f, 1.6f)) * openRoadBoost;
        float accel = aiPredictedAccel * Mathf.Sqrt(Mathf.Clamp(hurryFactor, 0.7f, 1.4f));

        if (currentSpeed >= effectiveTargetSpeed)
        {
            accel = Mathf.Min(accel, 0f);
        }

        if (hasPedestrianObstacle && !isChangingLane && nearestPedDist < 4.0f)
        {
            accel = -crashBrakeDecel;
            if (lightsController != null) lightsController.SetBraking(true);
            currentSpeed = Mathf.MoveTowards(currentSpeed, 0f, 20.0f * dt);
        }

        if (frontVehicleObj != null && distanceToFront < 3.2f)
        {
            accel = -crashBrakeDecel;
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

        float throttle = Mathf.Clamp(accel / Mathf.Max(1f, effectiveMaxAccel), -1f, 1f);

        currentSpeed = Mathf.Clamp(currentSpeed + accel * dt, 0f, effectiveTargetSpeed * 1.15f);

        float laneOffset = targetLaneX - currentPosX;
        float steering = 0f;
        float parallelSeparationVelX = 0f;

        float sideSeparationSteer = 0f;
        if (distToCarRight < 0.50f)
        {
            float urgency = (0.50f - distToCarRight) / 0.50f;
            sideSeparationSteer = -urgency * 0.45f;
            parallelSeparationVelX = -urgency * 1.5f;
            targetLaneX = Mathf.Clamp(currentPosX - 0.5f, -maxSafeRoadX + 0.35f, maxSafeRoadX - 0.35f);
        }
        else if (distToCarLeft < 0.50f)
        {
            float urgency = (0.50f - distToCarLeft) / 0.50f;
            sideSeparationSteer = urgency * 0.45f;
            parallelSeparationVelX = urgency * 1.5f;
            targetLaneX = Mathf.Clamp(currentPosX + 0.5f, -maxSafeRoadX + 0.35f, maxSafeRoadX - 0.35f);
        }

        if (currentPosX <= -maxSafeRoadX + 0.4f && parallelSeparationVelX < 0f) { parallelSeparationVelX = 0f; sideSeparationSteer = Mathf.Max(0f, sideSeparationSteer); }
        if (currentPosX >= maxSafeRoadX - 0.4f && parallelSeparationVelX > 0f) { parallelSeparationVelX = 0f; sideSeparationSteer = Mathf.Min(0f, sideSeparationSteer); }

        if (isChangingLane)
        {
            steering = Mathf.Clamp(laneOffset * 0.80f + aiSteeringOutput * 0.40f + sideSeparationSteer, -0.65f, 0.65f);
        }
        else if (Mathf.Abs(sideSeparationSteer) > 0.01f)
        {
            steering = Mathf.Clamp(sideSeparationSteer, -0.55f, 0.55f);
        }
        else
        {
            targetLaneX = currentPosX;
            steering = Mathf.Clamp(aiSteeringOutput * 0.35f, -0.20f, 0.20f);
            if (Mathf.Abs(steering) < 0.02f) steering = 0f;
        }

        smoothedSteering = Mathf.Lerp(smoothedSteering, steering, 12.0f * dt);
        steering = smoothedSteering;

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

        float currentYaw = transform.eulerAngles.y;
        if (currentYaw > 180f) currentYaw -= 360f;

        float desiredYaw = 0f;
        if (Mathf.Abs(steering) > 0.01f || isChangingLane || Mathf.Abs(laneOffset) > 0.08f || isCollidingWithVehicle || isCollidingWithBarrier)
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

        Vector3 targetVelocity = transform.forward * currentSpeed;
        targetVelocity.y = 0f;

        if (Mathf.Abs(parallelSeparationVelX) > 0.01f)
        {
            targetVelocity.x += parallelSeparationVelX;
        }
        else if (!isChangingLane && Mathf.Abs(laneOffset) <= 0.08f && !isCollidingWithVehicle && !isCollidingWithBarrier)
        {
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

        Vector3 curPos = rb.position;
        curPos.y = fixedHeightY;
        if (curPos.x > maxSafeRoadX) curPos.x = maxSafeRoadX - 0.15f;
        else if (curPos.x < -maxSafeRoadX) curPos.x = -maxSafeRoadX + 0.15f;
        rb.position = curPos;

        if (isCollidingWithBarrier && (Time.time - spawnTime >= 1.0f))
        {
            if (collisionVisualizer != null && !collisionVisualizer.IsInCrashState)
            {
                collisionVisualizer.TriggerCrash(3.0f);
            }
        }

        if (lightsController != null)
        {
            bool isBrakingNow = (throttle < -0.05f) || (distanceToFront < 6f && currentSpeed > 0.5f) || (collisionVisualizer != null && collisionVisualizer.IsInCrashState);
            lightsController.SetBraking(isBrakingNow);

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

        if (transform.position.z >= 490f)
        {
            transform.position = new Vector3(targetLaneX, fixedHeightY, 5f);
            currentSpeed = 5f;
            rb.linearVelocity = transform.forward * 5f;
        }

        if (myId == 0 && Time.frameCount % 50 == 0)
        {
            string frontLightStatus = isFrontCarBraking ? "PHANH ĐỎ" : (frontCarTurnSignal != TurnSignal.None ? "XI NHAN VÀNG" : "Tắt");
            string roadStatus = isOpenRoadAhead ? "ĐƯỜNG TRỐNG (Tăng tốc)" : $"Gần xe trước ({distanceToFront:F1}m)";
            Debug.Log($"[CarAgent 0] Tốc độ: {currentSpeed:F1}/{effectiveTargetSpeed:F1} m/s (Độ vội: {hurryFactor:F2}) | Ga: {throttle:F2} | Vị trí X: {transform.position.x:F1}m (Mép: {normalizedRoadOffset:F2}) | {roadStatus} | Đèn xe trước: {frontLightStatus}");
        }
    }

    void LateUpdate()
    {
        float yaw = transform.eulerAngles.y;
        if (yaw > 180f) yaw -= 360f;

        if (Mathf.Abs(yaw) > maxHeadingAngle || transform.forward.z <= 0.1f)
        {
            float clampedYaw = Mathf.Clamp(yaw, -maxHeadingAngle, maxHeadingAngle);
            if (transform.forward.z <= 0.1f)
            {
                clampedYaw = 0f;
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
        if (Time.time - spawnTime < 1.0f) return;

        GameObject other = collision.gameObject;

        if (other.CompareTag("Pedestrian") || other.GetComponentInParent<PedestrianAgent>() != null)
        {
            pedestrianCollisionPauseTimer = 3.5f;
            currentSpeed = 0f;
            if (rb != null) rb.linearVelocity = Vector3.zero;
            if (lightsController != null) lightsController.SetBraking(true);
            if (collisionVisualizer != null) collisionVisualizer.TriggerCrash(3.5f);

            IgnoreCollisionWithObject(other, 6.0f);

            var pedAgent = other.GetComponentInParent<PedestrianAgent>();
            if (pedAgent != null)
            {
                pedAgent.TriggerCrash(3.5f, gameObject);
                pedAgent.IgnoreCollisionWithVehicle(gameObject, 6.0f);
            }

            if (rb != null)
            {
                Vector3 p = rb.position;
                p.z -= 0.4f;
                rb.position = p;
                rb.linearVelocity = Vector3.zero;
            }
            return;
        }

        if (other.CompareTag("Vehicle") || other.GetComponentInParent<AIFVehicleAgent>() != null || other.GetComponentInParent<CarCollisionVisualizer>() != null)
        {
            isCollidingWithVehicle = true;
            vehicleCollisionTimer = 0.2f;

            if (collisionVisualizer != null) collisionVisualizer.TriggerCrash(3.0f);
            var otherVisualizer = other.GetComponentInParent<CarCollisionVisualizer>();
            if (otherVisualizer != null) otherVisualizer.TriggerCrash(3.0f);

            AIFVehicleAgent otherAgent = other.GetComponentInParent<AIFVehicleAgent>();
            Vector3 diff = transform.position - other.transform.position;
            diff.y = 0f;

            float dx = diff.x;
            float dz = diff.z;

            if (dz < -0.8f)
            {
                currentSpeed = 0f;
                if (lightsController != null) lightsController.SetBraking(true);

                Vector3 p = rb.position;
                p.z -= 0.15f;
                rb.position = p;
                vehicleSeparationVector = new Vector3(0f, 0f, -0.4f);

                if (otherAgent != null)
                {
                    otherAgent.currentSpeed = Mathf.Max(otherAgent.currentSpeed, 2.5f);
                    otherAgent.vehicleSeparationVector = new Vector3(0f, 0f, 0.4f);
                    otherAgent.vehicleCollisionTimer = 0.15f;
                }
            }
            else if (dz > 0.8f)
            {
                currentSpeed = Mathf.Max(currentSpeed, 2.5f);
                vehicleSeparationVector = new Vector3(0f, 0f, 0.4f);
            }
            else
            {
                float sideDir = (dx >= 0f) ? 1.0f : -1.0f;
                Vector3 p = rb.position;
                p.x += sideDir * 0.12f;
                rb.position = p;

                vehicleSeparationVector = new Vector3(sideDir * 0.6f, 0f, 0f);
            }

            return;
        }

        string name = other.name.ToLower();
        if (name == "road" || name.StartsWith("road")) return;

        if (name.Contains("guardrail") || name.Contains("barrier") || name.Contains("wall") || name.Contains("curb"))
        {
            isCollidingWithBarrier = true;
            barrierCollisionTimer = 0.25f;

            if (collisionVisualizer != null) collisionVisualizer.TriggerCrash(3.0f);

            float posX = transform.position.x;
            float curbRepelDir = -Mathf.Sign(posX);

            Vector3 p = rb.position;
            p.x += curbRepelDir * 0.15f;
            rb.position = p;

            barrierSeparationVector = new Vector3(curbRepelDir * 1.5f, 0f, 0.3f);
        }
    }

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

        Gizmos.color = Color.cyan;
        float[] fOffsets = new float[] { 0f, -0.22f, 0.22f, -0.45f, 0.45f, -0.68f, 0.68f, -0.90f, 0.90f };
        for (int i = 0; i < fOffsets.Length; i++)
        {
            Vector3 o = fCenter + transform.right * fOffsets[i];
            Gizmos.DrawRay(o, transform.forward * 25f);
        }

        Gizmos.color = Color.yellow;
        float[] dAngles = new float[] { -60f, -45f, -30f, -15f, 15f, 30f, 45f, 60f };
        for (int d = 0; d < dAngles.Length; d++)
        {
            Vector3 dir = Quaternion.Euler(0f, dAngles[d], 0f) * transform.forward;
            Gizmos.DrawRay(fCenter, dir * 18f);
        }

        Gizmos.color = Color.magenta;
        float[] sOffsetsZ = new float[] { 1.8f, 0.6f, -0.6f, -1.8f };
        for (int s = 0; s < sOffsetsZ.Length; s++)
        {
            Vector3 lO = cCenter + transform.forward * sOffsetsZ[s] - transform.right * 0.95f;
            Vector3 rO = cCenter + transform.forward * sOffsetsZ[s] + transform.right * 0.95f;
            Gizmos.DrawRay(lO, -transform.right * sideScanDistance);
            Gizmos.DrawRay(rO, transform.right * sideScanDistance);
        }

        Gizmos.color = Color.green;
        Gizmos.DrawLine(new Vector3(targetLaneX, fixedHeightY, transform.position.z - 5f), new Vector3(targetLaneX, fixedHeightY, transform.position.z + 15f));
    }
}
