using UnityEngine;

namespace TrafficJamSimulation
{
    public enum TurnSignal
    {
        None = 0,
        Left = 1,
        Right = 2,
        Hazard = 3
    }

    [DisallowMultipleComponent]
    public class CarLightsController : MonoBehaviour
    {
        [Header("Tail Light Renderers")]
        [Tooltip("Renderer của đèn sau bên trái")]
        public Renderer leftTailLightRenderer;

        [Tooltip("Renderer của đèn sau bên phải")]
        public Renderer rightTailLightRenderer;

        [Header("Light Colors")]
        [Tooltip("Màu khi đạp phanh (Đỏ rực)")]
        public Color brakeColor = new Color(1.0f, 0.05f, 0.05f, 1f);

        [Tooltip("Màu khi bật xi nhan (Vàng cam rực rỡ)")]
        public Color turnSignalColor = new Color(1.0f, 0.75f, 0.0f, 1f);

        [Tooltip("Màu đèn đuôi khi xe chạy bình thường (Đỏ sẫm dịu)")]
        public Color normalTailColor = new Color(0.25f, 0.02f, 0.02f, 1f);

        [Tooltip("Màu khi tắt đèn")]
        public Color offColor = new Color(0.08f, 0.08f, 0.08f, 1f);

        [Header("Signal State")]
        [SerializeField] private bool isBraking = false;
        [SerializeField] private TurnSignal currentSignal = TurnSignal.None;

        [Header("Blink Settings")]
        [Tooltip("Tần số nhấp nháy xi nhan (chu kỳ mỗi giây)")]
        public float blinkRate = 2.5f;

        [Header("Auto AI / Random Indicator (Demo)")]
        [Tooltip("Tự động kích hoạt xi nhan khi bẻ lái góc lớn hoặc chuyển làn")]
        public bool autoSignalFromSteering = true;

        private MaterialPropertyBlock _leftPropBlock;
        private MaterialPropertyBlock _rightPropBlock;

        public bool IsBraking => isBraking;
        public TurnSignal CurrentSignal => currentSignal;

        private void Awake()
        {
            _leftPropBlock = new MaterialPropertyBlock();
            _rightPropBlock = new MaterialPropertyBlock();

            FindRenderersIfNull();
        }

        private void Reset()
        {
            FindRenderersIfNull();
        }

        public void FindRenderersIfNull()
        {
            if (leftTailLightRenderer == null)
            {
                Transform leftT = transform.Find("RearLight_Left");
                if (leftT != null) leftTailLightRenderer = leftT.GetComponent<Renderer>();
            }

            if (rightTailLightRenderer == null)
            {
                Transform rightT = transform.Find("RearLight_Right");
                if (rightT != null) rightTailLightRenderer = rightT.GetComponent<Renderer>();
            }
        }

        public void SetBraking(bool braking)
        {
            isBraking = braking;
        }

        public void SetTurnSignal(TurnSignal signal)
        {
            currentSignal = signal;
        }

        public void TurnLeft()
        {
            currentSignal = (currentSignal == TurnSignal.Left) ? TurnSignal.None : TurnSignal.Left;
        }

        public void TurnRight()
        {
            currentSignal = (currentSignal == TurnSignal.Right) ? TurnSignal.None : TurnSignal.Right;
        }

        public void ToggleHazard()
        {
            currentSignal = (currentSignal == TurnSignal.Hazard) ? TurnSignal.None : TurnSignal.Hazard;
        }

        public void TurnOffSignals()
        {
            currentSignal = TurnSignal.None;
        }

        private void Update()
        {
            bool blinkOn = (Mathf.Repeat(Time.time * blinkRate, 1f) < 0.5f);

            Color leftColor = CalculateLightColor(isLeft: true, blinkOn);
            Color rightColor = CalculateLightColor(isLeft: false, blinkOn);

            ApplyColor(leftTailLightRenderer, _leftPropBlock, leftColor);
            ApplyColor(rightTailLightRenderer, _rightPropBlock, rightColor);
        }

        private Color CalculateLightColor(bool isLeft, bool blinkOn)
        {
            bool hasSignal = false;
            if (isLeft)
            {
                hasSignal = (currentSignal == TurnSignal.Left || currentSignal == TurnSignal.Hazard);
            }
            else
            {
                hasSignal = (currentSignal == TurnSignal.Right || currentSignal == TurnSignal.Hazard);
            }

            if (hasSignal)
            {
                if (blinkOn)
                {
                    return turnSignalColor;
                }
                else
                {
                    return isBraking ? brakeColor : offColor;
                }
            }

            if (isBraking)
            {
                return brakeColor;
            }

            return normalTailColor;
        }

        private void ApplyColor(Renderer rend, MaterialPropertyBlock propBlock, Color col)
        {
            if (rend == null) return;

            rend.GetPropertyBlock(propBlock);
            propBlock.SetColor("_Color", col);
            propBlock.SetColor("_BaseColor", col);
            propBlock.SetColor("_EmissionColor", col * 1.5f);
            rend.SetPropertyBlock(propBlock);
        }
    }
}
