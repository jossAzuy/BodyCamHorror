using System.Collections.Generic;
using KINEMATION.FPSAnimationPack.Scripts.Player;
using KINEMATION.FPSAnimationPack.Scripts.Weapon;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Bodycam
{
    /// <summary>
    /// Lowers the weapon smoothly when the player stands still, and raises it again when the
    /// player moves, aims, fires, reloads, etc.
    ///
    /// How it works: FPSPlayer builds the weapon pose every LateUpdate from the active weapon's
    /// settings (ikOffset + clavicle offsets), and the hands follow the weapon through IK.
    /// This script adds an extra offset on top of those values, scaled by a smoothed weight.
    /// It works on a runtime COPY of each weapon's settings, so your assets are never modified.
    /// When aiming, the pack already subtracts ikOffset from the ADS pose, so the sights come up
    /// to the camera correctly even if the weapon is lowered.
    ///
    /// Put it on the same object as BodycamPlayerController.
    /// </summary>
    public class BodycamWeaponLower : MonoBehaviour
    {
        [Header("References (auto-found if empty)")]
        [SerializeField] private CharacterController body;
        [SerializeField] private FPSPlayer fpsPlayer;
        [SerializeField] private PlayerInput playerInput;

        [Header("When to lower")]
        [Tooltip("Seconds standing still before the weapon starts to lower.")]
        [SerializeField] private float idleDelay = 2f;
        [Tooltip("Extra seconds the weapon stays up after aim / fire / reload etc.")]
        [SerializeField] private float combatHoldTime = 4f;
        [Tooltip("Horizontal speed (m/s) below which the player counts as standing still.")]
        [SerializeField] private float stillSpeedThreshold = 0.15f;
        [Tooltip("Action names that count as 'combat' and keep the weapon raised.")]
        [SerializeField] private string[] combatActionNames = { "Fire", "Aim", "Reload", "ThrowGrenade", "ChangeWeapon" };

        [Header("Lowered pose")]
        [Tooltip("Added to the weapon's Ik Offset at full lowering. x = right, y = up, z = forward (rig space).")]
        [SerializeField] private Vector3 lowerOffset = new Vector3(0.015f, -0.13f, -0.03f);
        [Tooltip("Added to both clavicle offsets at full lowering. Only change this if the arms look stretched.")]
        [SerializeField] private Vector3 lowerClavicleOffset = Vector3.zero;

        [Header("Walk bob (weapon moves relative to the camera)")]
        [Tooltip("Optional. Source of the step phase. Auto-found on this object if empty.")]
        [SerializeField] private BodycamMotion motion;
        [Tooltip("Meters at full walk: x = side sway, y = vertical bounce, z = forward/back. (0,0,0) disables it.")]
        [SerializeField] private Vector3 walkBobPosition = new Vector3(0.012f, 0.03f, 0.012f);
        [Tooltip("Radians the weapon trails behind the camera's bounce. Makes it feel heavy and held by the arms.")]
        [SerializeField] private float walkBobLag = 0.6f;
        [SerializeField] private float walkBobSmoothing = 10f;

        [Header("Timing")]
        [Tooltip("Seconds to lower (higher = slower, heavier).")]
        [SerializeField] private float lowerTime = 1.1f;
        [Tooltip("Seconds to raise back up (keep short so it never feels sluggish when something happens).")]
        [SerializeField] private float raiseTime = 0.3f;

        /// <summary>0 = raised, 1 = fully lowered.</summary>
        public float Lowered01 => _weight;

        private class Entry
        {
            public FPSWeaponSettings clone;
            public Vector3 baseIk, baseRight, baseLeft;
        }

        private readonly Dictionary<FPSWeapon, Entry> _entries = new Dictionary<FPSWeapon, Entry>();
        private InputAction[] _combatActions;

        private float _weight, _weightVel;
        private float _bobAmount;
        private float _sinceMoved, _sinceCombat;

        private void Start()
        {
            if (body == null) body = GetComponent<CharacterController>();
            if (fpsPlayer == null) fpsPlayer = FindFirstObjectByType<FPSPlayer>();
            if (playerInput == null && fpsPlayer != null) playerInput = fpsPlayer.GetComponentInChildren<PlayerInput>();
            if (motion == null) motion = GetComponent<BodycamMotion>();

            if (body == null || fpsPlayer == null)
            {
                Debug.LogError("BodycamWeaponLower: could not find CharacterController or FPSPlayer.", this);
                enabled = false;
                return;
            }

            if (playerInput != null && playerInput.actions != null)
            {
                _combatActions = new InputAction[combatActionNames.Length];
                for (int i = 0; i < combatActionNames.Length; i++)
                    _combatActions[i] = playerInput.actions.FindAction(combatActionNames[i], false);
            }

            _sinceMoved = 0f;
            _sinceCombat = combatHoldTime;
        }

        private bool CombatInputActive()
        {
            if (fpsPlayer.AdsWeight > 0.01f) return true;
            if (_combatActions == null) return false;

            foreach (var a in _combatActions)
            {
                if (a != null && (a.IsPressed() || a.WasPressedThisFrame())) return true;
            }
            return false;
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            // --- Decide the target ------------------------------------------------------------
            Vector3 v = body.velocity;
            bool moving = !body.isGrounded || new Vector2(v.x, v.z).magnitude > stillSpeedThreshold;
            _sinceMoved = moving ? 0f : _sinceMoved + dt;
            _sinceCombat = CombatInputActive() ? 0f : _sinceCombat + dt;

            bool lowered = _sinceMoved > idleDelay && _sinceCombat > combatHoldTime;

            float smooth = lowered ? lowerTime : raiseTime;
            _weight = Mathf.SmoothDamp(_weight, lowered ? 1f : 0f, ref _weightVel, Mathf.Max(0.01f, smooth));
            _weight = Mathf.Clamp01(_weight);

            // --- Apply to the active weapon's runtime settings copy ---------------------------
            var weapon = fpsPlayer.GetActiveWeapon();
            if (weapon == null || weapon.weaponSettings == null) return;

            if (!_entries.TryGetValue(weapon, out var e))
            {
                var clone = Instantiate(weapon.weaponSettings);
                e = new Entry
                {
                    clone = clone,
                    baseIk = clone.ikOffset,
                    baseRight = clone.rightClavicleOffset,
                    baseLeft = clone.leftClavicleOffset
                };
                weapon.weaponSettings = clone;
                _entries[weapon] = e;
            }

            // Walk bob: the weapon bounces relative to the camera (the whole rig already bobs with the camera).
            Vector3 bob = Vector3.zero;
            if (motion != null)
            {
                _bobAmount = Mathf.Lerp(_bobAmount, motion.WalkAmount, 1f - Mathf.Exp(-walkBobSmoothing * dt));
                float phase = motion.StepPhase - walkBobLag;
                float dip = -Mathf.Abs(Mathf.Sin(phase));
                bob = new Vector3(
                    Mathf.Cos(phase * 0.5f) * walkBobPosition.x,
                    dip * walkBobPosition.y,
                    dip * walkBobPosition.z) * _bobAmount;
            }

            e.clone.ikOffset = e.baseIk + lowerOffset * _weight + bob;
            e.clone.rightClavicleOffset = e.baseRight + lowerClavicleOffset * _weight;
            e.clone.leftClavicleOffset = e.baseLeft + lowerClavicleOffset * _weight;
        }

        private void OnDestroy()
        {
            foreach (var e in _entries.Values)
            {
                if (e.clone != null) Destroy(e.clone);
            }
            _entries.Clear();
        }
    }
}