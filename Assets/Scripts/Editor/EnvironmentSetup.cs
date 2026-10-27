using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace TrafficJamSimulation.Editor
{
    public static class EnvironmentSetup
    {
        private const string ScenePath = "Assets/Scenes/TrafficSimulationScene.unity";
        private const string RoadMaterialPath = "Assets/Materials/RoadMaterial.mat";
        private const string TailLightMaterialPath = "Assets/Materials/TailLightMaterial.mat";
        private const string CarBodyMaterialPath = "Assets/Materials/CarBodyMaterial.mat";
        private const string PrefabDirectory = "Assets/Prefabs";
        private const string CarAgentPrefabPath = "Assets/Prefabs/CarAgent.prefab";
        private const string PedestrianPrefabPath = "Assets/Prefabs/Pedestrian.prefab";
        private const string PedestrianMaterialPath = "Assets/Materials/PedestrianMaterial.mat";
        private const string OnnxModelPath = "Assets/Models/traffic_aif_agent.onnx";

        [MenuItem("Tools/Create CarAgent Prefab")]
        public static void CreateCarAgentPrefab()
        {
            Debug.Log("[EnvironmentSetup] Bắt đầu tạo CarAgent Prefab...");

            // 1. Đảm bảo tag Vehicle tồn tại
            EnsureTagExists("Vehicle");

            // 2. Đảm bảo thư mục Assets/Prefabs tồn tại
            if (!AssetDatabase.IsValidFolder(PrefabDirectory))
            {
                AssetDatabase.CreateFolder("Assets", "Prefabs");
            }

            // 3. Tạo Cube cơ bản
            GameObject carAgent = GameObject.CreatePrimitive(PrimitiveType.Cube);
            carAgent.name = "CarAgent";
            carAgent.tag = "Vehicle";
            carAgent.transform.position = Vector3.zero;
            carAgent.transform.rotation = Quaternion.identity;

            // 4. Chỉnh Scale: X: 1.8, Y: 1, Z: 4
            carAgent.transform.localScale = new Vector3(1.8f, 1f, 4f);

            // 5. Thêm Rigidbody với Mass = 1500, Freeze Position Y và Freeze toàn bộ Rotation (ngăn xe bay lên trời hoặc quay ngược chiều)
            Rigidbody rb = carAgent.GetComponent<Rigidbody>();
            if (rb == null) rb = carAgent.AddComponent<Rigidbody>();
            rb.mass = 1500f;
            rb.useGravity = false;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.constraints = RigidbodyConstraints.FreezePositionY | RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationY | RigidbodyConstraints.FreezeRotationZ;

            // 6. Thêm script AIFVehicleAgent
            AIFVehicleAgent aifAgent = carAgent.AddComponent<AIFVehicleAgent>();
            aifAgent.roadWidth = 10f;
            aifAgent.maxSafeRoadX = 4.1f;
            aifAgent.fixedHeightY = 0.5f;
            aifAgent.maxHeadingAngle = 18f;
            aifAgent.vehicleSeparationSpeed = 1.2f;
            aifAgent.curbSeparationSpeed = 2.5f;
            aifAgent.crashBrakeDecel = 12.0f;

            // 7. Tạo vật liệu đèn hậu (TailLightMaterial) nếu chưa có
            Material tailMat = AssetDatabase.LoadAssetAtPath<Material>(TailLightMaterialPath);
            if (tailMat == null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                tailMat = new Material(shader)
                {
                    name = "TailLightMaterial",
                    color = new Color(0.25f, 0.02f, 0.02f, 1f)
                };
                tailMat.EnableKeyword("_EMISSION");
                tailMat.SetColor("_EmissionColor", new Color(0.25f, 0.02f, 0.02f, 1f));
                AssetDatabase.CreateAsset(tailMat, TailLightMaterialPath);
                AssetDatabase.SaveAssets();
            }

            // 8. Tạo 2 đèn đuôi xe (Left & Right Tail Lights)
            // Đèn trái (RearLight_Left)
            GameObject leftLightObj = GameObject.CreatePrimitive(PrimitiveType.Cube);
            leftLightObj.name = "RearLight_Left";
            Object.DestroyImmediate(leftLightObj.GetComponent<Collider>());
            leftLightObj.transform.SetParent(carAgent.transform, false);
            leftLightObj.transform.localPosition = new Vector3(-0.38f, 0.05f, -0.505f);
            leftLightObj.transform.localRotation = Quaternion.identity;
            leftLightObj.transform.localScale = new Vector3(0.18f, 0.22f, 0.03f);
            var leftRenderer = leftLightObj.GetComponent<Renderer>();
            if (leftRenderer != null && tailMat != null) leftRenderer.sharedMaterial = tailMat;

            // Đèn phải (RearLight_Right)
            GameObject rightLightObj = GameObject.CreatePrimitive(PrimitiveType.Cube);
            rightLightObj.name = "RearLight_Right";
            Object.DestroyImmediate(rightLightObj.GetComponent<Collider>());
            rightLightObj.transform.SetParent(carAgent.transform, false);
            rightLightObj.transform.localPosition = new Vector3(0.38f, 0.05f, -0.505f);
            rightLightObj.transform.localRotation = Quaternion.identity;
            rightLightObj.transform.localScale = new Vector3(0.18f, 0.22f, 0.03f);
            var rightRenderer = rightLightObj.GetComponent<Renderer>();
            if (rightRenderer != null && tailMat != null) rightRenderer.sharedMaterial = tailMat;

            // 9. Thêm CarLightsController để điều khiển đèn phanh và xi nhan
            CarLightsController lightsController = carAgent.AddComponent<CarLightsController>();
            lightsController.leftTailLightRenderer = leftRenderer;
            lightsController.rightTailLightRenderer = rightRenderer;

            // Liên kết lightsController vào AIFVehicleAgent
            SerializedObject aifSo = new SerializedObject(aifAgent);
            SerializedProperty lightsProp = aifSo.FindProperty("lightsController");
            if (lightsProp != null)
            {
                lightsProp.objectReferenceValue = lightsController;
                aifSo.ApplyModifiedProperties();
            }

            // Gán vật liệu thân xe CarBodyMaterial
            Material bodyMat = AssetDatabase.LoadAssetAtPath<Material>(CarBodyMaterialPath);
            if (bodyMat != null)
            {
                var bodyRenderer = carAgent.GetComponent<Renderer>();
                if (bodyRenderer != null) bodyRenderer.sharedMaterial = bodyMat;
            }

            // 10. Thêm CarCollisionVisualizer để khi va chạm 2 xe cùng chuyển sang màu đỏ, va vào rào chắn cũng chuyển sang màu đỏ
            CarCollisionVisualizer collisionVis = carAgent.AddComponent<CarCollisionVisualizer>();
            collisionVis.bodyRenderer = carAgent.GetComponent<Renderer>();
            collisionVis.randomizeNormalColor = true;
            collisionVis.collisionColor = new Color(1.0f, 0.05f, 0.05f, 1f);
            collisionVis.collisionRedDuration = 2.0f;

            SerializedProperty visProp = aifSo.FindProperty("collisionVisualizer");
            if (visProp != null)
            {
                visProp.objectReferenceValue = collisionVis;
                aifSo.ApplyModifiedProperties();
            }

            // 11. Gán file ONNX model nếu có
            var modelAsset = AssetDatabase.LoadAssetAtPath<Unity.InferenceEngine.ModelAsset>(OnnxModelPath);
            if (modelAsset != null)
            {
                SerializedObject so = new SerializedObject(aifAgent);
                SerializedProperty prop = so.FindProperty("onnxModelAsset");
                if (prop != null)
                {
                    prop.objectReferenceValue = modelAsset;
                    so.ApplyModifiedProperties();
                    Debug.Log($"[EnvironmentSetup] Đã gán ModelAsset '{OnnxModelPath}' vào AIFVehicleAgent.");
                }
            }
            else
            {
                Debug.LogWarning($"[EnvironmentSetup] Chưa tìm thấy file '{OnnxModelPath}'. Prefab được tạo với onnxModelAsset để trống.");
            }

            // 8. Lưu thành Prefab trong Assets/Prefabs/
            PrefabUtility.SaveAsPrefabAsset(carAgent, CarAgentPrefabPath);

            // 9. Xóa GameObject tạm trong Hierarchy
            Object.DestroyImmediate(carAgent);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[EnvironmentSetup] Đã tạo thành công Prefab tại: {CarAgentPrefabPath}");
        }

        [MenuItem("Tools/Setup Road Environment")]
        public static void SetupEnvironment()
        {
            Debug.Log("[EnvironmentSetup] Bắt đầu thiết lập môi trường đường sá...");

            // 1. Đảm bảo Tag 'Vehicle' và 'Pedestrian' đã được đăng ký
            EnsureTagExists("Vehicle");
            EnsureTagExists("Pedestrian");

            // 2. Tạo hoặc mở Scene
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            // 3. Tạo vật liệu mặt đường (Asphalt Road Material)
            Material roadMat = AssetDatabase.LoadAssetAtPath<Material>(RoadMaterialPath);
            if (roadMat == null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                roadMat = new Material(shader)
                {
                    name = "RoadMaterial",
                    color = new Color(0.18f, 0.18f, 0.18f, 1f) // Màu nhựa đường tối
                };
                AssetDatabase.CreateAsset(roadMat, RoadMaterialPath);
                AssetDatabase.SaveAssets();
            }

            // 4. Bước 1 theo hướng dẫn: Tạo mặt đường Road (Plane)
            // Trong cửa sổ Hierarchy -> 3D Object -> Plane
            // Đặt tên là Road
            // Position: (0, 0, 250)
            // Scale: (1, 1, 50) -> tạo đoạn đường dài 500m theo trục Z
            GameObject road = GameObject.CreatePrimitive(PrimitiveType.Plane);
            road.name = "Road";
            road.transform.position = new Vector3(0f, 0f, 250f);
            road.transform.rotation = Quaternion.identity;
            road.transform.localScale = new Vector3(1f, 1f, 50f);

            var roadRenderer = road.GetComponent<Renderer>();
            if (roadRenderer != null && roadMat != null)
            {
                roadRenderer.sharedMaterial = roadMat;
            }

            // 4b. Tạo rào chắn 2 bên đường (Road Guardrails) ngăn tuyệt đối xe trượt rơi khỏi đường
            GameObject leftGuardrail = GameObject.CreatePrimitive(PrimitiveType.Cube);
            leftGuardrail.name = "LeftGuardrail";
            leftGuardrail.transform.position = new Vector3(-5.15f, 0.5f, 250f);
            leftGuardrail.transform.localScale = new Vector3(0.3f, 1.2f, 500f);
            var leftRend = leftGuardrail.GetComponent<Renderer>();
            if (leftRend != null && roadMat != null) leftRend.sharedMaterial = roadMat;

            GameObject rightGuardrail = GameObject.CreatePrimitive(PrimitiveType.Cube);
            rightGuardrail.name = "RightGuardrail";
            rightGuardrail.transform.position = new Vector3(5.15f, 0.5f, 250f);
            rightGuardrail.transform.localScale = new Vector3(0.3f, 1.2f, 500f);
            var rightRend = rightGuardrail.GetComponent<Renderer>();
            if (rightRend != null && roadMat != null) rightRend.sharedMaterial = roadMat;

            // 5. Bước 3 & Bước 5: Tạo TrafficSystem và gắn TrafficFieldManager, TrafficSpawner
            GameObject trafficSystem = new GameObject("TrafficSystem");
            trafficSystem.transform.position = Vector3.zero;
            trafficSystem.transform.rotation = Quaternion.identity;

            trafficSystem.AddComponent<TrafficFieldManager>();

            var spawner = trafficSystem.AddComponent<TrafficSpawner>();
            var carPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(CarAgentPrefabPath);
            if (carPrefab == null)
            {
                CreateCarAgentPrefab();
                carPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(CarAgentPrefabPath);
            }
            spawner.vehiclePrefab = carPrefab;
            spawner.numberOfVehicles = 40;
            spawner.lanes = new float[] { -2.4f, 0f, 2.4f };
            spawner.laneJitter = 0.15f;
            spawner.minSpawnIntervalZ = 8.5f;
            spawner.maxSpawnIntervalZ = 13.5f;
            spawner.spawnHeightY = 0.5f;
            spawner.checkPhysicsOverlap = true;

            // 5b. Thêm PedestrianSpawner để mô phỏng người qua đường
            var pedSpawner = trafficSystem.AddComponent<PedestrianSpawner>();
            var pedPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PedestrianPrefabPath);
            if (pedPrefab == null)
            {
                CreatePedestrianPrefab();
                pedPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PedestrianPrefabPath);
            }
            pedSpawner.pedestrianPrefab = pedPrefab;
            pedSpawner.autoSpawn = true;
            pedSpawner.roadWidth = 10f;
            pedSpawner.minSpawnZ = 40f;
            pedSpawner.maxSpawnZ = 450f;

            // 6. Bước 5: Căn chỉnh Camera chính để quan sát bao quát từ trên cao xuống
            Camera mainCam = Camera.main;
            if (mainCam != null)
            {
                mainCam.transform.position = new Vector3(0f, 45f, 50f);
                mainCam.transform.rotation = Quaternion.Euler(45f, 0f, 0f);
                mainCam.farClipPlane = 1000f;
            }

            // 7. Lưu Scene
            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log($"[EnvironmentSetup] Đã lưu Scene tại: {ScenePath}");

            // Đăng ký Scene vào Build Settings nếu chưa có
            AddSceneToBuildSettings(ScenePath);

            AssetDatabase.Refresh();
            Debug.Log("[EnvironmentSetup] Thiết lập môi trường hoàn tất thành công!");
        }

        [MenuItem("Tools/Create Pedestrian Prefab")]
        public static void CreatePedestrianPrefab()
        {
            Debug.Log("[EnvironmentSetup] Bắt đầu tạo Pedestrian Prefab (Khối chữ nhật đơn)...");
            EnsureTagExists("Pedestrian");

            if (!AssetDatabase.IsValidFolder(PrefabDirectory))
            {
                AssetDatabase.CreateFolder("Assets", "Prefabs");
            }

            // Tạo khối chữ nhật Cube duy nhất
            GameObject pedObj = GameObject.CreatePrimitive(PrimitiveType.Cube);
            pedObj.name = "Pedestrian";
            pedObj.tag = "Pedestrian";
            pedObj.transform.position = Vector3.zero;
            pedObj.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
            pedObj.transform.localScale = new Vector3(0.6f, 1.8f, 0.6f);

            // Cấu hình BoxCollider
            BoxCollider col = pedObj.GetComponent<BoxCollider>();
            if (col == null) col = pedObj.AddComponent<BoxCollider>();
            col.center = Vector3.zero;
            col.size = Vector3.one;

            // Cấu hình Rigidbody
            Rigidbody rb = pedObj.GetComponent<Rigidbody>();
            if (rb == null) rb = pedObj.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;

            // Cấu hình PedestrianAgent
            PedestrianAgent agent = pedObj.AddComponent<PedestrianAgent>();
            agent.walkingSpeed = 1.4f;
            agent.roadWidth = 10f;
            agent.stepFrequency = 4.0f;
            agent.stepBobAmplitude = 0.04f;

            // Vật liệu PedestrianMaterial
            Material pedMat = AssetDatabase.LoadAssetAtPath<Material>(PedestrianMaterialPath);
            if (pedMat == null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                pedMat = new Material(shader)
                {
                    name = "PedestrianMaterial",
                    color = new Color(0.95f, 0.72f, 0.12f, 1f)
                };
                AssetDatabase.CreateAsset(pedMat, PedestrianMaterialPath);
                AssetDatabase.SaveAssets();
            }

            var rend = pedObj.GetComponent<Renderer>();
            if (rend != null && pedMat != null) rend.sharedMaterial = pedMat;

            PrefabUtility.SaveAsPrefabAsset(pedObj, PedestrianPrefabPath);
            Object.DestroyImmediate(pedObj);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[EnvironmentSetup] Đã tạo thành công Prefab tại: {PedestrianPrefabPath}");
        }

        private static void EnsureTagExists(string tagName)
        {
            SerializedObject tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            SerializedProperty tagsProp = tagManager.FindProperty("tags");

            bool found = false;
            for (int i = 0; i < tagsProp.arraySize; i++)
            {
                SerializedProperty t = tagsProp.GetArrayElementAtIndex(i);
                if (t.stringValue.Equals(tagName))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                tagsProp.InsertArrayElementAtIndex(tagsProp.arraySize);
                SerializedProperty newTag = tagsProp.GetArrayElementAtIndex(tagsProp.arraySize - 1);
                newTag.stringValue = tagName;
                tagManager.ApplyModifiedProperties();
                Debug.Log($"[EnvironmentSetup] Đã thêm Tag '{tagName}' vào TagManager.");
            }
        }

        private static void AddSceneToBuildSettings(string scenePath)
        {
            var currentScenes = EditorBuildSettings.scenes;
            foreach (var s in currentScenes)
            {
                if (s.path == scenePath) return;
            }

            var newScenes = new EditorBuildSettingsScene[currentScenes.Length + 1];
            currentScenes.CopyTo(newScenes, 0);
            newScenes[newScenes.Length - 1] = new EditorBuildSettingsScene(scenePath, true);
            EditorBuildSettings.scenes = newScenes;
        }
    }
}
