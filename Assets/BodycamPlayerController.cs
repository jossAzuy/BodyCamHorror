using KINEMATION.FPSAnimationPack.Scripts.Sounds;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Bodycam
{
    /// <summary>
    /// Locomotion + look for a FPS Animation Pack rig.
    /// - This object owns the CharacterController and the yaw.
    /// - The FPSPlayer prefab (rigRoot) is detached and placed every frame so its camera
    ///   sits on this object's eye point, with yaw + pitch applied to the whole rig.
    /// - Speed is derived from the Animator "Gait" float that FPSPlayer already computes,
    ///   so movement and animation always agree.
    /// Runs before FPSCameraAnimator / Animator evaluation (execution order -100).
    /// </summary>
    [DefaultExecutionOrder(-100)]
    [RequireComponent(typeof(CharacterController))]
    public class BodycamPlayerController : MonoBehaviour
    {
        [Header("FPS Animation Pack rig")]
        [Tooltip("Root of the FPSPlayer prefab. It is detached from any parent at Awake.")]
        [SerializeField] private Transform rigRoot;
        [Tooltip("The Camera object inside the rig.")]
        [SerializeField] private Transform cameraPoint;
        [SerializeField] private Animator animator;
        [SerializeField] private FPSPlayerSound playerSound;

        [Header("Input")]
        [Tooltip("Optional. If set, actions are looked up by name here; missing ones fall back to defaults.")]
        [SerializeField] private PlayerInput playerInput;
        [SerializeField] private string moveActionName = "Move";
        [SerializeField] private string lookActionName = "Look";
        [SerializeField] private string jumpActionName = "Jump";

        [Header("Look")]
        [SerializeField] private float mouseSensitivity = 0.1f;     // degrees per pixel
        [SerializeField] private float gamepadSensitivity = 160f;   // degrees per second
        [SerializeField] private float pitchLimit = 80f;
        [Tooltip("0 = raw. Higher = snappier. Lower values give a heavier, bodycam-like lag.")]
        [SerializeField] private float lookSmoothing = 25f;
        [SerializeField] private bool lockCursor = true;

        [Header("Movement")]
        [SerializeField] private float walkSpeed = 2.4f;
        [SerializeField] private float sprintSpeed = 4.6f;
        [SerializeField] private float tacSprintSpeed = 5.6f;
        [SerializeField] private float acceleration = 30f;
        [SerializeField] private float deceleration = 40f;
        [Range(0f, 1f)] [SerializeField] private float airControl = 0.3f;

        [Header("Jump / gravity")]
        [SerializeField] private float jumpHeight = 0.9f;
        [SerializeField] private float gravity = 20f;
        [Tooltip("Seconds off the ground before IsInAir turns on (avoids flicker on stairs/ramps).")]
        [SerializeField] private float airGraceTime = 0.12f;
        [Tooltip("Minimum airtime to play the landing sound.")]
        [SerializeField] private float landSoundMinAirTime = 0.25f;

        [Header("Body")]
        [Tooltip("Eye height above the feet. The original prefab camera sits at ~1.654.")]
        [SerializeField] private float eyeHeight = 1.654f;

        private static readonly int GaitHash = Animator.StringToHash("Gait");
        private static readonly int IsInAirHash = Animator.StringToHash("IsInAir");

        private CharacterController _cc;
        private InputAction _move, _look, _jump;
        private bool _ownsActions;

        private Vector3 _cameraLocalInRig;
        private float _targetYaw, _targetPitch, _yaw, _pitch;
        private Vector3 _horizontalVelocity;
        private float _verticalVelocity;
        private float _airTime;
        private bool _wasGrounded = true;
        private bool _jumped;

        private void Reset()
        {
            var cc = GetComponent<CharacterController>();
            cc.height = 1.8f;
            cc.radius = 0.3f;
            cc.center = new Vector3(0f, 0.9f, 0f);
            cc.stepOffset = 0.35f;
        }

        private void Awake()
        {
            _cc = GetComponent<CharacterController>();

            if (rigRoot == null)
            {
                Debug.LogError("BodycamPlayerController: assign the FPSPlayer prefab root to 'rigRoot'.", this);
                enabled = false;
                return;
            }

            // The pack uses transform.root everywhere, so the rig must be a root object.
            rigRoot.SetParent(null, true);

            if (cameraPoint == null)
            {
                var cam = rigRoot.GetComponentInChildren<Camera>();
                if (cam != null) cameraPoint = cam.transform;
            }
            if (animator == null) animator = rigRoot.GetComponentInChildren<Animator>();
            if (playerSound == null) playerSound = rigRoot.GetComponentInChildren<FPSPlayerSound>();
            if (playerInput == null) playerInput = rigRoot.GetComponentInChildren<PlayerInput>();

            _cameraLocalInRig = rigRoot.InverseTransformPoint(cameraPoint.position);

            _targetYaw = _yaw = transform.eulerAngles.y;
            SetupInput();
        }

        private void OnEnable()
        {
            if (_ownsActions) { _move?.Enable(); _look?.Enable(); _jump?.Enable(); }
        }

        private void OnDisable()
        {
            if (_ownsActions) { _move?.Disable(); _look?.Disable(); _jump?.Disable(); }
        }

        private void Start()
        {
            if (lockCursor)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }

        private void SetupInput()
        {
            var asset = playerInput != null ? playerInput.actions : null;
            if (asset != null)
            {
                _move = asset.FindAction(moveActionName, false);
                _look = asset.FindAction(lookActionName, false);
                _jump = asset.FindAction(jumpActionName, false);
            }

            // Fallbacks (only created for actions that were not found).
            if (_move == null)
            {
                _move = new InputAction("Move", InputActionType.Value);
                _move.AddCompositeBinding("2DVector")
                    .With("Up", "<Keyboard>/w").With("Down", "<Keyboard>/s")
                    .With("Left", "<Keyboard>/a").With("Right", "<Keyboard>/d");
                _move.AddBinding("<Gamepad>/leftStick");
                _ownsActions = true;
            }
            if (_look == null)
            {
                _look = new InputAction("Look", InputActionType.Value);
                _look.AddBinding("<Pointer>/delta");
                _look.AddBinding("<Gamepad>/rightStick");
                _ownsActions = true;
            }
            if (_jump == null)
            {
                _jump = new InputAction("Jump", InputActionType.Button);
                _jump.AddBinding("<Keyboard>/space");
                _jump.AddBinding("<Gamepad>/buttonSouth");
                _ownsActions = true;
            }
            if (_ownsActions) { _move.Enable(); _look.Enable(); _jump.Enable(); }
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            UpdateLook(dt);
            UpdateMovement(dt);
            UpdateRig();
            UpdateAnimatorState(dt);
        }

        private void UpdateLook(float dt)
        {
            Vector2 look = _look.ReadValue<Vector2>();
            bool pad = _look.activeControl != null && _look.activeControl.device is Gamepad;
            look *= pad ? gamepadSensitivity * dt : mouseSensitivity;

            _targetYaw += look.x;
            _targetPitch = Mathf.Clamp(_targetPitch - look.y, -pitchLimit, pitchLimit);

            float a = lookSmoothing > 0f ? 1f - Mathf.Exp(-lookSmoothing * dt) : 1f;
            _yaw = Mathf.Lerp(_yaw, _targetYaw, a);
            _pitch = Mathf.Lerp(_pitch, _targetPitch, a);

            // Keep floats small over long sessions.
            if (Mathf.Abs(_targetYaw) > 720f) { _targetYaw -= 720f * Mathf.Sign(_targetYaw); _yaw -= 720f * Mathf.Sign(_yaw); }

            transform.rotation = Quaternion.Euler(0f, _yaw, 0f);
        }

        private float SpeedFromGait(float gait)
        {
            if (gait <= 1f) return gait * walkSpeed;
            if (gait <= 2f) return Mathf.Lerp(walkSpeed, sprintSpeed, gait - 1f);
            return Mathf.Lerp(sprintSpeed, tacSprintSpeed, Mathf.Clamp01(gait - 2f));
        }

        private void UpdateMovement(float dt)
        {
            bool grounded = _cc.isGrounded;

            Vector2 input = Vector2.ClampMagnitude(_move.ReadValue<Vector2>(), 1f);
            Vector3 wish = transform.right * input.x + transform.forward * input.y;
            Vector3 dir = wish.sqrMagnitude > 1e-4f ? wish.normalized : Vector3.zero;

            float gait = animator != null ? animator.GetFloat(GaitHash) : input.magnitude;
            Vector3 target = dir * SpeedFromGait(gait);

            float rate = dir == Vector3.zero ? deceleration : acceleration;
            if (!grounded) rate *= airControl;
            _horizontalVelocity = Vector3.MoveTowards(_horizontalVelocity, target, rate * dt);

            if (grounded && _verticalVelocity < 0f) _verticalVelocity = -2f;

            if (grounded && _jump.WasPressedThisFrame())
            {
                _verticalVelocity = Mathf.Sqrt(2f * gravity * jumpHeight);
                _jumped = true;
                if (playerSound != null) playerSound.PlayJumpSound();
            }

            _verticalVelocity -= gravity * dt;

            _cc.Move((_horizontalVelocity + Vector3.up * _verticalVelocity) * dt);

            // Landing detection uses the state after Move.
            bool groundedNow = _cc.isGrounded;
            if (groundedNow && !_wasGrounded)
            {
                if (_airTime >= landSoundMinAirTime && playerSound != null) playerSound.PlayLandSound();
                _jumped = false;
            }
            _airTime = groundedNow ? 0f : _airTime + dt;
            _wasGrounded = groundedNow;
        }

        private void UpdateRig()
        {
            Quaternion rot = Quaternion.Euler(_pitch, _yaw, 0f);
            Vector3 eye = transform.position + Vector3.up * eyeHeight;

            // Rotate around the camera point, not the rig origin, so the eye stays put.
            rigRoot.SetPositionAndRotation(eye - rot * _cameraLocalInRig, rot);
        }

        private void UpdateAnimatorState(float dt)
        {
            if (animator == null) return;

            // Overrides FPSPlayer's fixed 0.4s timer with the real ground state.
            bool inAir = !_wasGrounded && (_airTime > airGraceTime || _jumped);
            animator.SetBool(IsInAirHash, inAir);
        }
    }
}
