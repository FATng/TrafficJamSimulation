using UnityEngine;

namespace TrafficJamSimulation
{
    [DisallowMultipleComponent]
    public class CarCollisionVisualizer : MonoBehaviour
    {
        [Header("Renderer")]
        [Tooltip("MeshRenderer thân xe chính (nếu để trống sẽ tự lấy từ GameObject)")]
        public Renderer bodyRenderer;

        [Header("Color Settings")]
        [Tooltip("Màu xe khi chạy bình thường (Tuyệt đối không dùng màu đỏ, màu đỏ chỉ dành cho cảnh báo va chạm)")]
        public Color normalColor = new Color(0.92f, 0.93f, 0.96f, 1f);

        [Tooltip("Tự động gán màu ngẫu nhiên trang nhã (không có màu đỏ) khi xuất phát để dễ phân biệt các xe")]
        public bool randomizeNormalColor = true;

        [Tooltip("Màu sắc khi xảy ra va chạm (Đỏ rực cảnh báo)")]
        public Color collisionColor = new Color(1.0f, 0.05f, 0.05f, 1f);

        [Header("Collision Timer")]
        [Tooltip("Thời gian duy trì màu đỏ sau va chạm (giây)")]
        public float collisionRedDuration = 3.0f;

        private MaterialPropertyBlock _propBlock;
        private float _redTimer = 0f;
        private bool _isCurrentlyColliding = false;
        private float _spawnTime = 0f;

        public bool IsInCrashState => _isCurrentlyColliding || _redTimer > 0f;

        public static readonly Color[] SafeColorPalette = new Color[]
        {
            new Color(0.92f, 0.93f, 0.96f, 1f),
            new Color(0.15f, 0.45f, 0.90f, 1f),
            new Color(0.10f, 0.68f, 0.40f, 1f),
            new Color(0.15f, 0.72f, 0.88f, 1f),
            new Color(0.20f, 0.80f, 0.58f, 1f),
            new Color(0.85f, 0.85f, 0.25f, 1f),
            new Color(0.32f, 0.35f, 0.40f, 1f),
            new Color(0.12f, 0.22f, 0.48f, 1f),
            new Color(0.16f, 0.17f, 0.19f, 1f)
        };

        public static bool IsReddish(Color c)
        {
            return (c.r > 0.50f && (c.r - c.g > 0.12f) && (c.r - c.b > 0.12f));
        }

        private void Awake()
        {
            _spawnTime = Time.time;

            if (bodyRenderer == null)
            {
                bodyRenderer = GetComponent<Renderer>();
            }

            _propBlock = new MaterialPropertyBlock();

            if (randomizeNormalColor)
            {
                normalColor = SafeColorPalette[Random.Range(0, SafeColorPalette.Length)];
            }

            if (IsReddish(normalColor))
            {
                normalColor = SafeColorPalette[0];
            }

            ApplyColor(normalColor, false);
        }

        public void SetNormalColor(Color color)
        {
            normalColor = IsReddish(color) ? SafeColorPalette[0] : color;
            if (!IsInCrashState)
            {
                ApplyColor(normalColor, false);
            }
        }

        public void TriggerCrash(float duration = -1f)
        {
            if (Time.time - _spawnTime < 1.0f) return;

            float dur = (duration > 0f) ? duration : collisionRedDuration;
            _redTimer = Mathf.Max(_redTimer, dur);
            ApplyColor(collisionColor, true);
            Debug.Log($"<color=red><b>[VA CHẠM]</b> {gameObject.name} (Tọa độ: {transform.position}) </color>");
        }

        public void ResetColor()
        {
            _redTimer = 0f;
            _isCurrentlyColliding = false;
            ApplyColor(normalColor, false);
        }

        private void OnCollisionEnter(Collision collision)
        {
            HandleCollision(collision);
        }

        private void OnCollisionStay(Collision collision)
        {
            HandleCollision(collision);
        }

        private void OnCollisionExit(Collision collision)
        {
            _isCurrentlyColliding = false;
        }

        private void HandleCollision(Collision collision)
        {
            if (Time.time - _spawnTime < 1.0f) return;

            GameObject other = collision.gameObject;

            if (other.CompareTag("Vehicle") || other.GetComponentInParent<AIFVehicleAgent>() != null || other.GetComponentInParent<CarCollisionVisualizer>() != null)
            {
                _isCurrentlyColliding = true;
                TriggerCrash(3.0f);

                var otherVisualizer = other.GetComponentInParent<CarCollisionVisualizer>();
                if (otherVisualizer != null)
                {
                    otherVisualizer.TriggerCrash(3.0f);
                }
                return;
            }

            string otherName = other.name.ToLower();
            if (otherName == "road" || otherName.StartsWith("road")) return;

            if (otherName.Contains("guardrail") || otherName.Contains("barrier") || otherName.Contains("wall") || otherName.Contains("curb"))
            {
                _isCurrentlyColliding = true;
                TriggerCrash(3.0f);
            }
        }

        private void Update()
        {
            if (_isCurrentlyColliding)
            {
                _redTimer = collisionRedDuration;
                ApplyColor(collisionColor, true);
                return;
            }

            if (_redTimer > 0f)
            {
                _redTimer -= Time.deltaTime;

                if (_redTimer <= 0.6f)
                {
                    float t = Mathf.Clamp01(_redTimer / 0.6f);
                    Color blended = Color.Lerp(normalColor, collisionColor, t);
                    ApplyColor(blended, t > 0.4f);
                }
                else
                {
                    ApplyColor(collisionColor, true);
                }

                if (_redTimer <= 0f)
                {
                    ApplyColor(normalColor, false);
                }
            }
        }

        private void ApplyColor(Color col, bool isEmissive)
        {
            if (bodyRenderer == null)
            {
                bodyRenderer = GetComponent<Renderer>();
                if (bodyRenderer == null) return;
            }

            if (_propBlock == null) _propBlock = new MaterialPropertyBlock();
            bodyRenderer.GetPropertyBlock(_propBlock);
            _propBlock.SetColor("_Color", col);
            _propBlock.SetColor("_BaseColor", col);
            if (isEmissive)
            {
                _propBlock.SetColor("_EmissionColor", col * 2.0f);
            }
            else
            {
                _propBlock.SetColor("_EmissionColor", Color.black);
            }
            bodyRenderer.SetPropertyBlock(_propBlock);

            Material[] mats = bodyRenderer.materials;
            if (mats != null && mats.Length > 0)
            {
                for (int i = 0; i < mats.Length; i++)
                {
                    Material mat = mats[i];
                    if (mat == null) continue;
                    mat.color = col;
                    if (mat.HasProperty("_Color")) mat.SetColor("_Color", col);
                    if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", col);
                    if (isEmissive)
                    {
                        mat.EnableKeyword("_EMISSION");
                        if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", col * 2.0f);
                    }
                    else
                    {
                        mat.DisableKeyword("_EMISSION");
                        if (mat.HasProperty("_EmissionColor")) mat.SetColor("_EmissionColor", Color.black);
                    }
                }
            }
        }

        [ContextMenu("Test/Trigger Red Collision")]
        public void TestCrash() => TriggerCrash(3f);

        [ContextMenu("Test/Reset Normal Color")]
        public void TestReset() => ResetColor();
    }
}
