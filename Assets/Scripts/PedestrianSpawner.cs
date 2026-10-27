using System.Collections.Generic;
using UnityEngine;

namespace TrafficJamSimulation
{
    /// <summary>
    /// Quản lý việc sinh người đi bộ qua đường (Pedestrian Spawner):
    /// - Tự động sinh người đi bộ định kỳ tại các vị trí ngẫu nhiên dọc theo cung đường.
    /// - Hỗ trợ gọi hàm sinh người qua đường theo yêu cầu hoặc vị trí chỉ định.
    /// - Quản lý hướng đi (Trái sang Phải hoặc Phải sang Trái), tốc độ và màu sắc trang phục.
    /// </summary>
    public class PedestrianSpawner : MonoBehaviour
    {
        [Header("Pedestrian Prefab")]
        [Tooltip("Prefab của người qua đường (gán từ Assets/Prefabs/Pedestrian.prefab)")]
        public GameObject pedestrianPrefab;

        [Header("Auto Spawn Settings")]
        [Tooltip("Bật/tắt tự động sinh người qua đường theo chu kỳ")]
        public bool autoSpawn = true;

        [Tooltip("Khoảng thời gian tối thiểu giữa 2 lần sinh (giây)")]
        public float minSpawnInterval = 6.0f;

        [Tooltip("Khoảng thời gian tối đa giữa 2 lần sinh (giây)")]
        public float maxSpawnInterval = 12.0f;

        [Tooltip("Giới hạn số lượng người qua đường đồng thời để tối ưu hiệu năng")]
        public int maxSimultaneousPedestrians = 8;

        [Header("Road & Position Parameters")]
        [Tooltip("Bề rộng mặt đường (m)")]
        public float roadWidth = 10f;

        [Tooltip("Tọa độ Z tối thiểu trên đường để người qua đường xuất hiện")]
        public float minSpawnZ = 35f;

        [Tooltip("Tọa độ Z tối đa trên đường để người qua đường xuất hiện")]
        public float maxSpawnZ = 450f;

        [Tooltip("Cao độ sàn xuất phát (m)")]
        public float spawnHeightY = 0.9f;

        [Header("Speed Settings")]
        [Tooltip("Tốc độ đi bộ tối thiểu (m/s)")]
        public float minWalkingSpeed = 1.2f;

        [Tooltip("Tốc độ đi bộ tối đa (m/s)")]
        public float maxWalkingSpeed = 1.8f;

        [Header("Designated Crossings (Tùy chọn)")]
        [Tooltip("Danh sách các vị trí Z cố định có vạch kẻ đường cho người đi bộ (nếu có)")]
        public float[] designatedCrossings = new float[] { 100f, 250f, 380f };

        [Tooltip("Tỉ lệ ưu tiên sinh người tại các vạch kẻ cố định (0.0 đến 1.0)")]
        [Range(0f, 1f)]
        public float designatedCrossingBias = 0.5f;

        private float _nextSpawnTimer = 0f;
        private List<PedestrianAgent> _activePedestrians = new List<PedestrianAgent>();
        private Transform _pedestrianContainer;

        // Bảng màu trang phục người đi bộ
        private static readonly Color[] ClothingPalette = new Color[]
        {
            new Color(0.95f, 0.75f, 0.10f), // Vàng cam ấm
            new Color(0.20f, 0.55f, 0.90f), // Xanh dương
            new Color(0.15f, 0.75f, 0.45f), // Xanh lá cây
            new Color(0.85f, 0.30f, 0.20f), // Cam đỏ
            new Color(0.60f, 0.25f, 0.85f), // Tím thạch anh
            new Color(0.20f, 0.80f, 0.85f), // Xanh ngọc
            new Color(0.90f, 0.90f, 0.90f), // Trắng xám
            new Color(0.35f, 0.35f, 0.40f)  // Xám đậm
        };

        private void Awake()
        {
            // Tạo GameObject container để gom gọn Hierarchy
            GameObject container = new GameObject("PedestrianContainer");
            container.transform.SetParent(transform);
            _pedestrianContainer = container.transform;
        }

        private void Start()
        {
            ScheduleNextSpawn();
        }

        private void Update()
        {
            // Dọn dẹp danh sách các người đã hoàn thành qua đường
            _activePedestrians.RemoveAll(p => p == null || p.HasCompletedCrossing);

            if (!autoSpawn) return;

            _nextSpawnTimer -= Time.deltaTime;
            if (_nextSpawnTimer <= 0f)
            {
                if (_activePedestrians.Count < maxSimultaneousPedestrians)
                {
                    SpawnPedestrian();
                }
                ScheduleNextSpawn();
            }
        }

        private void ScheduleNextSpawn()
        {
            _nextSpawnTimer = Random.Range(minSpawnInterval, maxSpawnInterval);
        }

        /// <summary>
        /// Hàm sinh ngẫu nhiên một người đi bộ qua đường.
        /// </summary>
        /// <returns>GameObject của người đi bộ vừa được sinh</returns>
        public GameObject SpawnPedestrian()
        {
            if (pedestrianPrefab == null)
            {
                Debug.LogWarning("[PedestrianSpawner] Chưa gán pedestrianPrefab!");
                return null;
            }

            // Chọn vị trí Z: hoặc là tại vạch kẻ cố định, hoặc ngẫu nhiên dọc đường
            float spawnZ;
            if (designatedCrossings != null && designatedCrossings.Length > 0 && Random.value < designatedCrossingBias)
            {
                int index = Random.Range(0, designatedCrossings.Length);
                spawnZ = designatedCrossings[index] + Random.Range(-1.5f, 1.5f);
            }
            else
            {
                spawnZ = Random.Range(minSpawnZ, maxSpawnZ);
            }

            // Hướng đi: ngẫu nhiên từ trái sang phải hoặc từ phải sang trái
            bool fromLeftToRight = (Random.value > 0.5f);
            float speed = Random.Range(minWalkingSpeed, maxWalkingSpeed);

            return SpawnPedestrianAt(spawnZ, fromLeftToRight, speed);
        }

        /// <summary>
        /// Hàm sinh người đi bộ tại vị trí Z chỉ định với hướng và tốc độ tùy chọn.
        /// </summary>
        /// <param name="zPosition">Vị trí theo trục Z của đường</param>
        /// <param name="fromLeftToRight">True nếu đi từ lề trái (+X) sang lề phải, False nếu ngược lại</param>
        /// <param name="walkingSpeed">Tốc độ di chuyển của người qua đường (m/s)</param>
        /// <returns>GameObject của người đi bộ vừa được sinh</returns>
        public GameObject SpawnPedestrianAt(float zPosition, bool fromLeftToRight = true, float walkingSpeed = 1.4f)
        {
            if (pedestrianPrefab == null)
            {
                Debug.LogWarning("[PedestrianSpawner] Chưa gán pedestrianPrefab!");
                return null;
            }

            float halfRoad = roadWidth * 0.5f;
            int direction = fromLeftToRight ? 1 : -1;
            // Xuất phát cách mép đường 0.8m
            float startX = fromLeftToRight ? (-halfRoad - 0.8f) : (halfRoad + 0.8f);

            Vector3 spawnPos = new Vector3(startX, spawnHeightY, zPosition);
            Quaternion spawnRot = Quaternion.Euler(0f, fromLeftToRight ? 90f : -90f, 0f);

            GameObject pedObj = Instantiate(pedestrianPrefab, spawnPos, spawnRot, _pedestrianContainer);
            pedObj.name = $"Pedestrian_{_activePedestrians.Count + 1}_{zPosition:F0}m";

            PedestrianAgent agent = pedObj.GetComponent<PedestrianAgent>();
            if (agent == null)
            {
                agent = pedObj.AddComponent<PedestrianAgent>();
            }

            agent.Initialize(spawnPos, direction, walkingSpeed, roadWidth);

            // Gán màu ngẫu nhiên cho trang phục
            Color randomClothing = ClothingPalette[Random.Range(0, ClothingPalette.Length)];
            agent.SetRandomColor(randomClothing);

            _activePedestrians.Add(agent);

            return pedObj;
        }

        /// <summary>
        /// Dọn dẹp toàn bộ người đi bộ đang có trên đường
        /// </summary>
        public void ClearAllPedestrians()
        {
            foreach (var ped in _activePedestrians)
            {
                if (ped != null) Destroy(ped.gameObject);
            }
            _activePedestrians.Clear();
        }

        private void OnDrawGizmosSelected()
        {
            // Vẽ các vị trí vạch kẻ qua đường trong Scene view
            if (designatedCrossings == null) return;
            Gizmos.color = Color.yellow;
            foreach (var z in designatedCrossings)
            {
                Vector3 center = new Vector3(0f, 0.05f, z);
                Gizmos.DrawWireCube(center, new Vector3(roadWidth, 0.1f, 3.0f));
            }
        }
    }
}
