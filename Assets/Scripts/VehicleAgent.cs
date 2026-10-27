using UnityEngine;

namespace TrafficJamSimulation
{
    public enum ControlMode
    {
        IntelligentDriverModel, // Rule-based IDM (benchmark)
        SentisInference         // AI Inference via ONNX model
    }

    /// <summary>
    /// Represents an individual vehicle operating in a single-lane traffic flow.
    /// </summary>
    [RequireComponent(typeof(Renderer))]
    public class VehicleAgent : MonoBehaviour
    {
        [Header("Vehicle Configuration")]
        [Tooltip("Unique ID of the vehicle in the lane sequence")]
        public int vehicleId;

        [Tooltip("Control mode used to calculate acceleration")]
        public ControlMode controlMode = ControlMode.IntelligentDriverModel;

        [Header("Physical State")]
        public float currentSpeed = 10f; // m/s
        public float maxSpeed = 25f;     // m/s (~90 km/h)
        public float maxAcceleration = 2.0f; // m/s^2
        public float desiredDeceleration = 2.5f; // m/s^2
        public float minimumGap = 2.0f;  // m (s0)
        public float desiredHeadwayTime = 1.5f; // seconds (T)
        public float vehicleLength = 4.5f; // meters

        [Header("Driver Personality (Độ Vội)")]
        [Range(0.6f, 1.6f)]
        [Tooltip("Độ vội vàng của tài xế: quyết định tốc độ tối đa mong muốn và khoảng cách bám đuôi.")]
        public float hurryFactor = 1.0f;

        [Header("Track Information")]
        [Tooltip("Current linear position along the lane (0 to trackLength)")]
        public float trackPosition = 0f;

        [Header("Leader Reference")]
        public VehicleAgent leaderVehicle;
        public float distanceToLeader;
        public float relativeVelocity; // leader.speed - currentSpeed

        [Header("Visualization")]
        public Color freeFlowColor = Color.green;
        public Color jammedColor = Color.red;

        private Renderer _renderer;
        private MaterialPropertyBlock _propBlock;
        private TrafficInferenceEngine _inferenceEngine;
        private CarLightsController _lightsController;

        private void Awake()
        {
            _renderer = GetComponent<Renderer>();
            _propBlock = new MaterialPropertyBlock();
            _lightsController = GetComponent<CarLightsController>();
        }

        public void Initialize(int id, float initialPosition, float initialSpeed, TrafficInferenceEngine engine)
        {
            vehicleId = id;
            trackPosition = initialPosition;
            currentSpeed = initialSpeed;
            _inferenceEngine = engine;
        }

        public void SetLeader(VehicleAgent leader)
        {
            leaderVehicle = leader;
        }

        /// <summary>
        /// Updates vehicle physics each simulation step.
        /// </summary>
        public void StepSimulation(float deltaTime, float trackLength, bool isLoopTrack)
        {
            UpdateLeaderPerception(trackLength, isLoopTrack);

            float acceleration = 0f;

            if (controlMode == ControlMode.SentisInference && _inferenceEngine != null && _inferenceEngine.IsModelReady)
            {
                // Run inference via ONNX model (Normalized inputs: speed, gap, rel_speed)
                acceleration = _inferenceEngine.PredictAcceleration(currentSpeed, distanceToLeader, relativeVelocity);
            }
            else
            {
                // Fallback to Intelligent Driver Model (IDM)
                acceleration = CalculateIDMAcceleration();
            }

            // Apply acceleration (Xe có hurryFactor cao bứt tốc nhanh hơn khi đường trống)
            if (distanceToLeader > 25f && hurryFactor > 1.0f && acceleration > 0f)
            {
                acceleration *= (1.0f + (hurryFactor - 1.0f) * 1.5f);
            }
            currentSpeed = Mathf.Clamp(currentSpeed + acceleration * deltaTime, 0f, maxSpeed * hurryFactor);

            // Update tail lights (Braking when decelerating)
            if (_lightsController != null)
            {
                _lightsController.SetBraking(acceleration < -0.3f);
            }

            // Update linear position
            trackPosition += currentSpeed * deltaTime;
            if (isLoopTrack && trackLength > 0f)
            {
                trackPosition %= trackLength;
            }

            // Update visual color based on speed (Red = Jammed, Green = Free Flow)
            UpdateVisualFeedback();
        }

        private void UpdateLeaderPerception(float trackLength, bool isLoopTrack)
        {
            if (leaderVehicle == null)
            {
                distanceToLeader = 999f;
                relativeVelocity = 0f;
                return;
            }

            float rawDistance = leaderVehicle.trackPosition - trackPosition;
            if (isLoopTrack && rawDistance < 0f)
            {
                rawDistance += trackLength;
            }

            distanceToLeader = Mathf.Max(0.01f, rawDistance - vehicleLength);
            relativeVelocity = leaderVehicle.currentSpeed - currentSpeed;
        }

        /// <summary>
        /// Standard Intelligent Driver Model (Treiber et al., 2000)
        /// Widely used to model phantom traffic jams and shockwaves.
        /// </summary>
        private float CalculateIDMAcceleration()
        {
            float effMaxSpeed = maxSpeed * hurryFactor;
            float effMinGap = minimumGap / Mathf.Clamp(hurryFactor, 0.7f, 1.4f);

            // Free road acceleration term
            float freeRoadRatio = currentSpeed / Mathf.Max(0.1f, effMaxSpeed);
            float freeRoadTerm = 1f - Mathf.Pow(freeRoadRatio, 4f);

            // Tín hiệu đèn phanh xe trước
            bool leaderIsBraking = false;
            if (leaderVehicle != null)
            {
                CarLightsController leaderLights = leaderVehicle.GetComponent<CarLightsController>();
                if (leaderLights != null && leaderLights.IsBraking) leaderIsBraking = true;
            }

            // Interaction / braking term
            float deltaV = -relativeVelocity; // v - v_lead
            float desiredDistance = effMinGap + (leaderIsBraking ? 2.5f : 0f) + Mathf.Max(0f, currentSpeed * desiredHeadwayTime + 
                (currentSpeed * deltaV) / (2f * Mathf.Sqrt(maxAcceleration * desiredDeceleration)));

            float interactionTerm = Mathf.Pow(desiredDistance / Mathf.Max(0.1f, distanceToLeader), 2f);

            float acceleration = maxAcceleration * (freeRoadTerm - interactionTerm);
            return Mathf.Clamp(acceleration, -desiredDeceleration * 3f, maxAcceleration);
        }

        private void UpdateVisualFeedback()
        {
            if (_renderer == null) return;

            float speedRatio = Mathf.Clamp01(currentSpeed / Mathf.Max(0.1f, maxSpeed));
            Color currentColor = Color.Lerp(jammedColor, freeFlowColor, speedRatio);

            _renderer.GetPropertyBlock(_propBlock);
            _propBlock.SetColor("_BaseColor", currentColor);
            _propBlock.SetColor("_Color", currentColor); // Fallback for built-in/standard shader
            _renderer.SetPropertyBlock(_propBlock);
        }
    }
}
