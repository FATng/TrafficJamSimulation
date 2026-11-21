using UnityEngine;

namespace TrafficJamSimulation
{
    public enum ControlMode
    {
        IntelligentDriverModel,
        SentisInference
    }

    [RequireComponent(typeof(Renderer))]
    public class VehicleAgent : MonoBehaviour
    {
        [Header("Vehicle Configuration")]
        [Tooltip("Unique ID of the vehicle in the lane sequence")]
        public int vehicleId;

        [Tooltip("Control mode used to calculate acceleration")]
        public ControlMode controlMode = ControlMode.IntelligentDriverModel;

        [Header("Physical State")]
        public float currentSpeed = 10f;
        public float maxSpeed = 25f;
        public float maxAcceleration = 2.0f;
        public float desiredDeceleration = 2.5f;
        public float minimumGap = 2.0f;
        public float desiredHeadwayTime = 1.5f;
        public float vehicleLength = 4.5f;

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
        public float relativeVelocity;

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

        public void StepSimulation(float deltaTime, float trackLength, bool isLoopTrack)
        {
            UpdateLeaderPerception(trackLength, isLoopTrack);

            float acceleration = 0f;

            if (controlMode == ControlMode.SentisInference && _inferenceEngine != null && _inferenceEngine.IsModelReady)
            {
                acceleration = _inferenceEngine.PredictAcceleration(currentSpeed, distanceToLeader, relativeVelocity);
            }
            else
            {
                acceleration = CalculateIDMAcceleration();
            }

            if (distanceToLeader > 25f && hurryFactor > 1.0f && acceleration > 0f)
            {
                acceleration *= (1.0f + (hurryFactor - 1.0f) * 1.5f);
            }
            currentSpeed = Mathf.Clamp(currentSpeed + acceleration * deltaTime, 0f, maxSpeed * hurryFactor);

            if (_lightsController != null)
            {
                _lightsController.SetBraking(acceleration < -0.3f);
            }

            trackPosition += currentSpeed * deltaTime;
            if (isLoopTrack && trackLength > 0f)
            {
                trackPosition %= trackLength;
            }

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

        private float CalculateIDMAcceleration()
        {
            float effMaxSpeed = maxSpeed * hurryFactor;
            float effMinGap = minimumGap / Mathf.Clamp(hurryFactor, 0.7f, 1.4f);

            float freeRoadRatio = currentSpeed / Mathf.Max(0.1f, effMaxSpeed);
            float freeRoadTerm = 1f - Mathf.Pow(freeRoadRatio, 4f);

            bool leaderIsBraking = false;
            if (leaderVehicle != null)
            {
                CarLightsController leaderLights = leaderVehicle.GetComponent<CarLightsController>();
                if (leaderLights != null && leaderLights.IsBraking) leaderIsBraking = true;
            }

            float deltaV = -relativeVelocity;
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
            _propBlock.SetColor("_Color", currentColor);
            _renderer.SetPropertyBlock(_propBlock);
        }
    }
}
