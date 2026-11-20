using UnityEngine;

namespace Bodycam
{
    /// <summary>
    /// Chest-mounted camera motion, added ON TOP of what BodycamPlayerController does.
    /// The controller places the rig every Update(); this runs in LateUpdate and applies
    /// an offset around the camera point, so nothing accumulates frame to frame.
    ///  - Walk/sprint bob (vertical + roll + pitch), scaled by speed
    ///  - Turn inertia: the camera rolls and lags when you turn quickly
    ///  - Landing / jump springs
    ///  - Constant tiny handheld noise
    /// Put it on the same object as BodycamPlayerController.
    /// </summary>
    [DefaultExecutionOrder(200)]
    public class BodycamMotion : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private Transform rigRoot;
        [SerializeField] private Transform cameraPoint;
        [SerializeField] private CharacterController body;
        [SerializeField] private float sprintReference = 5.6f;

        [Header("Walk bob")]
        [Tooltip("Steps per meter. Higher = faster bobbing.")]
        [SerializeField] private float stepsPerMeter = 0.85f;
        [SerializeField] private float bobHeight = 0.035f;
        [SerializeField] private float bobRoll = 1.4f;     // degrees
        [SerializeField] private float bobPitch = 0.9f;    // degrees
        [SerializeField] private float bobSideways = 0.015f;

        [Header("Turn inertia")]
        [SerializeField] private float turnRoll = 0.035f;  // degrees of roll per deg/s of yaw rate
        [SerializeField] private float maxTurnRoll = 6f;
        [SerializeField] private float turnSmoothing = 10f;

        [Header("Springs (jump / landing)")]
        [SerializeField] private float landingKick = 1.2f;  // degrees of pitch per m/s of fall speed
        [SerializeField] private float jumpKick = -2.5f;
        [SerializeField] private float springStiffness = 90f;
        [SerializeField] private float springDamping = 12f;

        [Header("Handheld noise")]
        [SerializeField] private float noiseAngle = 0.25f; // degrees
        [SerializeField] private float noiseSpeed = 0.9f;

        [Header("Breathing")]
        [SerializeField] private bool breathingEnabled = true;
        [Tooltip("Breaths per second while resting (0.25 = one breath every 4 s).")]
        [SerializeField] private float restBreathRate = 0.25f;
        [Tooltip("Breaths per second at full exhaustion.")]
        [SerializeField] private float tiredBreathRate = 0.9f;
        [Tooltip("Chest pitch in degrees at rest / exhausted.")]
        [SerializeField] private float restBreathPitch = 0.25f;
        [SerializeField] private float tiredBreathPitch = 1.1f;
        [Tooltip("Vertical chest movement in meters at rest / exhausted.")]
        [SerializeField] private float restBreathHeight = 0.004f;
        [SerializeField] private float tiredBreathHeight = 0.018f;
        [Tooltip("Seconds of continuous sprint to reach full exhaustion.")]
        [SerializeField] private float secondsToExhaust = 12f;
        [Tooltip("Seconds to fully recover once you stop sprinting.")]
        [SerializeField] private float secondsToRecover = 14f;
        [Tooltip("Speed (0..1 of sprintReference) above which effort builds up.")]
        [Range(0f, 1f)] [SerializeField] private float effortThreshold = 0.6f;
        [Tooltip("Remaining breath movement while holding breath (0 = frozen).")]
        [Range(0f, 1f)] [SerializeField] private float holdBreathAmount = 0.1f;

        /// <summary>0 = rested, 1 = exhausted. Handy for audio later.</summary>
        public float Fatigue => _fatigue;
        /// <summary>Current breath cycle, 0..1 (rising = inhale). Handy for audio later.</summary>
        public float BreathCycle01 => Mathf.Repeat(_breathPhase, 1f);
        public bool IsHoldingBreath => _holdBreath;

        /// <summary>Hold the breath (steady aim, hiding, etc.). Breathing eases back when released.</summary>
        public void HoldBreath(bool hold) => _holdBreath = hold;

        private float _fatigue;
        private float _breathPhase;
        private float _breathAmp = 1f;
        private bool _holdBreath;

        private float _phase;
        private float _smoothedRoll;
        private float _lastYaw;
        private float _springPos, _springVel;
        private float _lastVy;
        private bool _wasGrounded = true;
        private float _s01;

        private void Awake()
        {
            if (body == null) body = GetComponent<CharacterController>();
            if (rigRoot == null || cameraPoint == null)
            {
                var ctrl = GetComponent<BodycamPlayerController>();
                Debug.LogWarning("BodycamMotion: assign rigRoot and cameraPoint.", this);
            }
            _lastYaw = transform.eulerAngles.y;
        }

        private void LateUpdate()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f || rigRoot == null || cameraPoint == null || body == null) return;

            Vector3 vel = body.velocity;
            float speed = new Vector2(vel.x, vel.z).magnitude;
            bool grounded = body.isGrounded;
            _s01 = Mathf.Lerp(_s01, Mathf.Clamp01(speed / sprintReference), 1f - Mathf.Exp(-8f * dt));

            // --- Bob (only advances while grounded and moving)
            if (grounded) _phase += speed * stepsPerMeter * Mathf.PI * dt;
            float amt = grounded ? Mathf.Clamp01(speed / 1.5f) : 0f;
            float sprintBoost = 1f + _s01 * 0.8f;
            float sinP = Mathf.Sin(_phase);
            float cosP = Mathf.Cos(_phase * 0.5f);

            float bobY = -Mathf.Abs(Mathf.Sin(_phase)) * bobHeight * amt * sprintBoost;
            float bobX = cosP * bobSideways * amt * sprintBoost;
            float roll = cosP * bobRoll * amt * sprintBoost;
            float pitch = sinP * bobPitch * amt * sprintBoost;

            // --- Turn inertia (roll while turning)
            float yaw = transform.eulerAngles.y;
            float yawRate = Mathf.DeltaAngle(_lastYaw, yaw) / dt;
            _lastYaw = yaw;
            float targetRoll = Mathf.Clamp(-yawRate * turnRoll, -maxTurnRoll, maxTurnRoll);
            _smoothedRoll = Mathf.Lerp(_smoothedRoll, targetRoll, 1f - Mathf.Exp(-turnSmoothing * dt));
            roll += _smoothedRoll;

            // --- Springs: landing / jump
            if (grounded && !_wasGrounded) _springVel += _lastVy * landingKick * 8f; // lastVy is negative
            if (!grounded && _wasGrounded && vel.y > 1f) _springVel += jumpKick * 8f;
            if (!grounded) _lastVy = vel.y;
            _wasGrounded = grounded;

            _springVel += (-springStiffness * _springPos - springDamping * _springVel) * dt;
            _springPos += _springVel * dt;
            pitch += _springPos;
            bobY += _springPos * 0.004f;

            // --- Breathing (chest rising/falling, faster and deeper with fatigue)
            if (breathingEnabled)
            {
                bool exerting = grounded && _s01 > effortThreshold;
                if (exerting) _fatigue += dt / Mathf.Max(0.1f, secondsToExhaust);
                else _fatigue -= dt / Mathf.Max(0.1f, secondsToRecover);
                _fatigue = Mathf.Clamp01(_fatigue);

                float rate = Mathf.Lerp(restBreathRate, tiredBreathRate, _fatigue);
                _breathPhase += rate * dt;

                float targetAmp = _holdBreath ? holdBreathAmount : 1f;
                _breathAmp = Mathf.Lerp(_breathAmp, targetAmp, 1f - Mathf.Exp(-(_holdBreath ? 6f : 3f) * dt));

                // Slightly asymmetric wave: inhale a bit faster than exhale.
                float w = Mathf.Sin(_breathPhase * Mathf.PI * 2f);
                w += 0.25f * Mathf.Sin(_breathPhase * Mathf.PI * 4f);

                float pitchAmp = Mathf.Lerp(restBreathPitch, tiredBreathPitch, _fatigue) * _breathAmp;
                float heightAmp = Mathf.Lerp(restBreathHeight, tiredBreathHeight, _fatigue) * _breathAmp;
                pitch -= w * pitchAmp;   // inhale tilts the camera slightly up
                bobY += w * heightAmp;
            }

            // --- Handheld noise
            float t = Time.time * noiseSpeed;
            float nPitch = (Mathf.PerlinNoise(t, 1.3f) - 0.5f) * 2f * noiseAngle;
            float nYaw = (Mathf.PerlinNoise(t, 7.1f) - 0.5f) * 2f * noiseAngle;
            float nRoll = (Mathf.PerlinNoise(t, 13.7f) - 0.5f) * 2f * noiseAngle;
            pitch += nPitch; roll += nRoll;

            // --- Apply around the camera point
            Vector3 pivot = cameraPoint.position;
            rigRoot.position += cameraPoint.up * bobY + cameraPoint.right * bobX;
            pivot = cameraPoint.position;
            rigRoot.RotateAround(pivot, cameraPoint.right, pitch);
            rigRoot.RotateAround(pivot, cameraPoint.up, nYaw);
            rigRoot.RotateAround(pivot, cameraPoint.forward, roll);
        }
    }
}