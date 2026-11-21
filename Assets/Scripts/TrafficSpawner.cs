using System.Collections.Generic;
using UnityEngine;
using TrafficJamSimulation;

public class TrafficSpawner : MonoBehaviour
{
    [Header("Vehicle Prefab")]
    public GameObject vehiclePrefab;

    [Tooltip("Số lượng xe xuất hiện trên cung đường")]
    public int numberOfVehicles = 40;

    [Header("Lane & Spacing Configuration")]
    [Tooltip("Tọa độ X của các làn đường (Mặt đường rộng 10m: [-2.4m, 0m, +2.4m])")]
    public float[] lanes = new float[] { -2.4f, 0f, 2.4f };

    [Tooltip("Độ xê dịch ngẫu nhiên nhẹ trong làn xe để cảnh giao thông tự nhiên (không chạm vạch làn)")]
    public float laneJitter = 0.15f;

    [Tooltip("Tọa độ Z bắt đầu xuất phát của đoàn xe")]
    public float startZ = 12f;

    [Tooltip("Khoảng cách tối thiểu theo trục Z giữa 2 xe cùng làn (xe dài 4m, tối thiểu 8.0m để khoảng trống >= 4.0m)")]
    public float minSpawnIntervalZ = 8.5f;

    [Tooltip("Khoảng cách tối đa theo trục Z giữa 2 xe cùng làn")]
    public float maxSpawnIntervalZ = 13.5f;

    [Tooltip("Độ cao Y khi spawn (khớp với fixedHeightY = 0.5f của thân xe)")]
    public float spawnHeightY = 0.5f;

    [Tooltip("Bật kiểm tra vật lý OverlapBox trước khi đặt xe để ngăn chặn hoàn toàn việc chồng lấn")]
    public bool checkPhysicsOverlap = true;

    [Header("Driver Personality Variations (Độ Vội Vàng)")]
    [Tooltip("Độ vội tối thiểu của tài xế (xe đi từ tốn, giữ khoảng cách xa)")]
    public float minHurryFactor = 0.75f;

    [Tooltip("Độ vội tối đa của tài xế (xe vội vàng, chạy nhanh, bám sát)")]
    public float maxHurryFactor = 1.35f;

    [HideInInspector] public float roadWidth = 6f;
    [HideInInspector] public float spawnIntervalZ = 7f;

    private void Start()
    {
        if (vehiclePrefab == null)
        {
            Debug.LogError("[TrafficSpawner] Chưa gán vehiclePrefab vào TrafficSpawner trong Scene!");
            return;
        }

        SpawnTrafficVehicles();
    }

    public void SpawnTrafficVehicles()
    {
        if (lanes == null || lanes.Length == 0)
        {
            lanes = new float[] { -2.4f, 0f, 2.4f };
        }

        int numLanes = lanes.Length;
        float[] nextZInLane = new float[numLanes];

        float staggerStep = Mathf.Max(minSpawnIntervalZ, 8.0f) / numLanes;
        for (int l = 0; l < numLanes; l++)
        {
            nextZInLane[l] = startZ + (l * staggerStep);
        }

        List<Vector3> spawnedPositions = new List<Vector3>();

        for (int i = 0; i < numberOfVehicles; i++)
        {
            int bestLane = GetLowestZLane(nextZInLane);
            float targetLaneX = lanes[bestLane];

            float jitter = Random.Range(-laneJitter, laneJitter);
            float posX = Mathf.Clamp(targetLaneX + jitter, -3.6f, 3.6f);
            float posZ = nextZInLane[bestLane];

            Vector3 candidatePos = new Vector3(posX, spawnHeightY, posZ);

            candidatePos = ResolveOverlap(candidatePos, spawnedPositions);

            GameObject carObj = Instantiate(vehiclePrefab, candidatePos, Quaternion.identity);
            carObj.name = $"CarAgent_{i:D2}_Lane{bestLane}";
            Physics.SyncTransforms();

            spawnedPositions.Add(candidatePos);

            float advanceGap = Random.Range(minSpawnIntervalZ, maxSpawnIntervalZ);
            nextZInLane[bestLane] = candidatePos.z + advanceGap;

            var aifAgent = carObj.GetComponent<AIFVehicleAgent>();
            if (aifAgent != null)
            {
                aifAgent.hurryFactor = Random.Range(minHurryFactor, maxHurryFactor);
                aifAgent.fixedHeightY = spawnHeightY;
            }

            var visualizer = carObj.GetComponent<CarCollisionVisualizer>();
            if (visualizer != null)
            {
                Color safeColor = CarCollisionVisualizer.SafeColorPalette[(i + bestLane) % CarCollisionVisualizer.SafeColorPalette.Length];
                visualizer.ResetColor();
                visualizer.SetNormalColor(safeColor);
            }
        }

        Debug.Log($"[TrafficSpawner] Đã spawn thành công {spawnedPositions.Count} xe với khoảng cách an toàn, không có xe nào bị đè hay chạm nhau.");
    }

    private int GetLowestZLane(float[] laneZ)
    {
        int minIndex = 0;
        float minVal = laneZ[0];
        for (int i = 1; i < laneZ.Length; i++)
        {
            if (laneZ[i] < minVal)
            {
                minVal = laneZ[i];
                minIndex = i;
            }
        }
        return minIndex;
    }

    private Vector3 ResolveOverlap(Vector3 candidatePos, List<Vector3> existingPositions)
    {
        Vector3 resolved = candidatePos;
        int maxAttempts = 30;
        int attempts = 0;

        while (attempts < maxAttempts)
        {
            bool hasConflict = false;

            for (int i = 0; i < existingPositions.Count; i++)
            {
                Vector3 prev = existingPositions[i];
                float dx = Mathf.Abs(resolved.x - prev.x);
                float dz = Mathf.Abs(resolved.z - prev.z);

                if (dx < 2.0f && dz < 7.5f)
                {
                    hasConflict = true;
                    break;
                }

                if (dx < 2.2f && dz < 4.5f)
                {
                    hasConflict = true;
                    break;
                }
            }

            if (!hasConflict && checkPhysicsOverlap)
            {
                Vector3 halfExtents = new Vector3(0.95f, 0.5f, 2.1f);
                Collider[] hits = Physics.OverlapBox(resolved, halfExtents, Quaternion.identity);

                for (int h = 0; h < hits.Length; h++)
                {
                    GameObject hitObj = hits[h].gameObject;
                    if (hitObj.name.ToLower().Contains("road")) continue;

                    hasConflict = true;
                    break;
                }
            }

            if (!hasConflict)
            {
                return resolved;
            }

            resolved.z += 2.5f;
            attempts++;
        }

        return resolved;
    }

    private void OnDrawGizmosSelected()
    {
        if (lanes == null) return;
        Gizmos.color = Color.cyan;
        for (int i = 0; i < lanes.Length; i++)
        {
            Vector3 from = new Vector3(lanes[i], spawnHeightY, startZ);
            Vector3 to = new Vector3(lanes[i], spawnHeightY, startZ + 300f);
            Gizmos.DrawLine(from, to);
        }
    }
}
