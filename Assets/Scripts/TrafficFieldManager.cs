using UnityEngine;

public class TrafficFieldManager : MonoBehaviour
{
    public static TrafficFieldManager Instance { get; private set; }

    [Header("Cấu hình Lưới Mật Độ")]
    public int cellCount = 50;
    public float roadLength = 500f;
    private float[] densityMap;

    void Awake()
    {
        if (Instance == null) Instance = this;
        else Destroy(gameObject);

        densityMap = new float[cellCount];
    }

    public void RegisterVehiclePosition(Vector3 worldPos)
    {
        float z = Mathf.Clamp(worldPos.z, 0, roadLength - 0.01f);
        int cellIndex = Mathf.FloorToInt((z / roadLength) * cellCount);
        densityMap[cellIndex] += 1.0f;
    }

    public float GetDensityAt(Vector3 worldPos)
    {
        float z = Mathf.Clamp(worldPos.z, 0, roadLength - 0.01f);
        int cellIndex = Mathf.FloorToInt((z / roadLength) * cellCount);
        return Mathf.Clamp01(densityMap[cellIndex] / 10.0f);
    }

    void LateUpdate()
    {
        // Phục hồi và làm mượt mật độ theo thời gian
        for (int i = 0; i < cellCount; i++)
        {
            densityMap[i] = Mathf.MoveTowards(densityMap[i], 0f, Time.deltaTime * 5f);
        }
    }
}