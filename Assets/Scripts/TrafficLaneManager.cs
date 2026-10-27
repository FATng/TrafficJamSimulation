using System.Collections.Generic;
using UnityEngine;

namespace TrafficJamSimulation
{
    public enum TrackType
    {
        CircularRing,
        StraightHighway
    }

    /// <summary>
    /// Spawns and manages single-lane traffic flow, demonstrates shockwave propagation (phantom jams),
    /// and tracks real-time traffic performance metrics.
    /// </summary>
    public class TrafficLaneManager : MonoBehaviour
    {
        [Header("Lane & Track Settings")]
        public TrackType trackType = TrackType.StraightHighway;
        [Tooltip("Radius of the circular ring road in meters")]
        public float circleRadius = 50f;
        [Tooltip("Length of the straight track in meters")]
        public float straightTrackLength = 500f;

        [Header("Vehicle Population")]
        [Tooltip("Prefab containing VehicleAgent component (assigned from Assets/Prefabs)")]
        public GameObject vehiclePrefab;
        [Range(5, 50)]
        public int vehicleCount = 22;
        public float initialSpeed = 12f; // ~43 km/h
        public ControlMode vehicleControlMode = ControlMode.IntelligentDriverModel;

        [Header("Perturbation Trigger (Phantom Jam Experiment)")]
        [Tooltip("Inject sudden braking into one vehicle to induce stop-and-go shockwaves")]
        public bool triggerBrakePerturbation = true;
        public float perturbationDelaySeconds = 8.0f;
        public float brakeDurationSeconds = 2.0f;

        [Header("Runtime Metrics (Read-Only)")]
        [SerializeField] private float averageSpeed;
        [SerializeField] private float minimumSpeed;
        [SerializeField] private int jammedVehicleCount;

        private List<VehicleAgent> _vehicles = new List<VehicleAgent>();
        private TrafficInferenceEngine _inferenceEngine;
        private float _simulationTimer = 0f;
        private bool _perturbationApplied = false;

        public float EffectiveTrackLength => trackType == TrackType.CircularRing 
            ? 2f * Mathf.PI * circleRadius 
            : straightTrackLength;

        private void Start()
        {
            _inferenceEngine = GetComponent<TrafficInferenceEngine>();
            SpawnVehicles();
        }

        public void SpawnVehicles()
        {
            // Clear existing
            foreach (var v in _vehicles)
            {
                if (v != null) Destroy(v.gameObject);
            }
            _vehicles.Clear();

            float totalLength = EffectiveTrackLength;
            float spacing = totalLength / Mathf.Max(1, vehicleCount);

            for (int i = 0; i < vehicleCount; i++)
            {
                float initialPos = i * spacing;
                Vector3 spawnPos = Calculate3DPosition(initialPos);
                Quaternion spawnRot = Calculate3DRotation(initialPos);

                GameObject carGo;
                if (vehiclePrefab != null)
                {
                    carGo = Instantiate(vehiclePrefab, spawnPos, spawnRot, transform);
                }
                else
                {
                    // Fallback procedural placeholder vehicle (Box)
                    carGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    carGo.transform.SetPositionAndRotation(spawnPos, spawnRot);
                    carGo.transform.localScale = new Vector3(2f, 1.4f, 4.2f);
                    carGo.transform.SetParent(transform);
                }

                carGo.name = $"Vehicle_{i:D2}";
                carGo.tag = "Vehicle";
                var agent = carGo.GetComponent<VehicleAgent>();
                if (agent == null)
                {
                    agent = carGo.AddComponent<VehicleAgent>();
                }

                agent.controlMode = vehicleControlMode;
                agent.Initialize(i, initialPos, initialSpeed, _inferenceEngine);
                _vehicles.Add(agent);
            }

            // Link circular leader-follower relationships
            for (int i = 0; i < _vehicles.Count; i++)
            {
                int leaderIndex = (i + 1) % _vehicles.Count;
                _vehicles[i].SetLeader(_vehicles[leaderIndex]);
            }
        }

        private void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;
            _simulationTimer += dt;
            float trackLength = EffectiveTrackLength;

            // Handle perturbation test (Brake on car 0)
            HandlePerturbation();

            // Step each vehicle
            float totalSpeed = 0f;
            float minSpeed = float.MaxValue;
            int jammedCount = 0;

            for (int i = 0; i < _vehicles.Count; i++)
            {
                var vehicle = _vehicles[i];
                vehicle.StepSimulation(dt, trackLength, isLoopTrack: true);

                // Update physical 3D transform
                vehicle.transform.position = Calculate3DPosition(vehicle.trackPosition);
                vehicle.transform.rotation = Calculate3DRotation(vehicle.trackPosition);

                // Compute stats
                totalSpeed += vehicle.currentSpeed;
                if (vehicle.currentSpeed < minSpeed) minSpeed = vehicle.currentSpeed;
                if (vehicle.currentSpeed < 3.0f) jammedCount++;
            }

            // Update Inspector metrics
            if (_vehicles.Count > 0)
            {
                averageSpeed = totalSpeed / _vehicles.Count;
                minimumSpeed = minSpeed;
                jammedVehicleCount = jammedCount;
            }
        }

        private void HandlePerturbation()
        {
            if (!triggerBrakePerturbation || _vehicles.Count == 0) return;

            if (!_perturbationApplied && _simulationTimer >= perturbationDelaySeconds)
            {
                if (_simulationTimer <= perturbationDelaySeconds + brakeDurationSeconds)
                {
                    // Force car 0 to brake hard
                    _vehicles[0].currentSpeed = Mathf.Max(0f, _vehicles[0].currentSpeed - 8.0f * Time.fixedDeltaTime);
                }
                else
                {
                    _perturbationApplied = true;
                }
            }
        }

        public Vector3 Calculate3DPosition(float linearPos)
        {
            if (trackType == TrackType.CircularRing)
            {
                float angle = (linearPos / EffectiveTrackLength) * 2f * Mathf.PI;
                float x = circleRadius * Mathf.Cos(angle);
                float z = circleRadius * Mathf.Sin(angle);
                return transform.position + new Vector3(x, 0.7f, z);
            }
            else
            {
                return transform.position + new Vector3(0f, 0.7f, linearPos);
            }
        }

        public Quaternion Calculate3DRotation(float linearPos)
        {
            if (trackType == TrackType.CircularRing)
            {
                float angle = (linearPos / EffectiveTrackLength) * 2f * Mathf.PI;
                // Tangent direction to the circle
                Vector3 forward = new Vector3(-Mathf.Sin(angle), 0f, Mathf.Cos(angle));
                return Quaternion.LookRotation(forward, Vector3.up);
            }
            else
            {
                return Quaternion.LookRotation(Vector3.forward, Vector3.up);
            }
        }

        private void OnDrawGizmos()
        {
            Gizmos.color = Color.cyan;
            if (trackType == TrackType.CircularRing)
            {
                int segments = 64;
                Vector3 center = transform.position;
                Vector3 prevPoint = center + new Vector3(circleRadius, 0f, 0f);

                for (int i = 1; i <= segments; i++)
                {
                    float angle = (i / (float)segments) * 2f * Mathf.PI;
                    Vector3 currentPoint = center + new Vector3(circleRadius * Mathf.Cos(angle), 0f, circleRadius * Mathf.Sin(angle));
                    Gizmos.DrawLine(prevPoint, currentPoint);
                    prevPoint = currentPoint;
                }
            }
            else
            {
                Gizmos.DrawLine(transform.position, transform.position + new Vector3(0f, 0f, straightTrackLength));
            }
        }
    }
}
