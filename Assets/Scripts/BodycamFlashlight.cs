using System;
using System.Collections.Generic;
using KINEMATION.FPSAnimationPack.Scripts.Player;
using KINEMATION.FPSAnimationPack.Scripts.Weapon;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Bodycam
{
    /// <summary>
    /// Weapon-mounted flashlight.
    /// - A spot light is created under the active weapon's aimPoint (or you can author your own:
    ///   add a child Light named "Flashlight" to the weapon prefab and it will be used instead).
    /// - Because it is a child of the weapon, it follows sway, recoil, aiming and the
    ///   lowered pose from BodycamWeaponLower for free.
    /// - Smooth fade, short unstable flicker on turn-on, optional battery with low-battery flicker.
    /// Put it on the same object as BodycamPlayerController.
    /// </summary>
    public class BodycamFlashlight : MonoBehaviour
    {
        [Header("References (auto-found if empty)")]
        [SerializeField] private FPSPlayer fpsPlayer;

        [Header("Input")]
        [SerializeField] private string keyboardBinding = "<Keyboard>/f";
        [SerializeField] private string gamepadBinding = "<Gamepad>/dpad/up";
        [SerializeField] private bool startOn = false;

        [Header("Mount (relative to the weapon's aimPoint)")]
        [Tooltip("Meters. z should be slightly past the muzzle so the weapon does not shadow its own beam.")]
        [SerializeField] private Vector3 positionOffset = new Vector3(0f, -0.03f, 0.25f);
        [Tooltip("Degrees. Change this if the beam does not point along the barrel.")]
        [SerializeField] private Vector3 rotationOffset = Vector3.zero;

        [Header("Light")]
        [SerializeField] private Color color = new Color(0.90f, 0.95f, 1f);
        [SerializeField] private float intensity = 12f;
        [SerializeField] private float range = 28f;
        [Range(10f, 120f)] [SerializeField] private float spotAngle = 52f;
        [Range(1f, 119f)] [SerializeField] private float innerSpotAngle = 18f;
        [SerializeField] private LightShadows shadows = LightShadows.Soft;

        [Header("Behaviour")]
        [Tooltip("Seconds to fade in / out.")]
        [SerializeField] private float fadeTime = 0.08f;
        [Tooltip("Seconds of unstable flicker right after turning on.")]
        [SerializeField] private float startupFlickerTime = 0.25f;
        [Tooltip("Constant, very subtle intensity wobble (0 = none).")]
        [SerializeField, Range(0f, 0.2f)] private float baseFlicker = 0.015f;
        [SerializeField] private float flickerSpeed = 7f;

        [Header("Battery (optional)")]
        [Tooltip("Minutes of continuous use from full to empty. 0 = infinite battery.")]
        [SerializeField] private float batteryMinutes = 0f;
        [Range(0f, 1f)] [SerializeField] private float lowBatteryThreshold = 0.2f;
        [SerializeField, Range(0f, 1f)] private float lowBatteryFlicker = 0.35f;
        [Tooltip("Dim factor at 0% battery before it dies.")]
        [SerializeField, Range(0.1f, 1f)] private float lowBatteryDim = 0.5f;
        [Tooltip("Random dropouts per second at near-empty battery.")]
        [SerializeField] private float dropoutsPerSecond = 1.5f;

        [Header("Audio (optional)")]
        [SerializeField] private AudioClip clickOn;
        [SerializeField] private AudioClip clickOff;
        [SerializeField, Range(0f, 1f)] private float clickVolume = 0.6f;

        public bool IsOn => _isOn;
        /// <summary>1 = full, 0 = empty. Always 1 when the battery is infinite.</summary>
        public float Battery01 => _battery;

        private class Entry
        {
            public Light light;
            public float baseIntensity;
            public bool authored;
        }

        private readonly Dictionary<FPSWeapon, Entry> _entries = new Dictionary<FPSWeapon, Entry>();
        private InputAction _toggle;
        private AudioSource _audio;

        private bool _isOn;
        private float _power;
        private float _battery = 1f;
        private float _startup;
        private float _dropoutRemaining;

        private void Awake()
        {
            _toggle = new InputAction("Flashlight", InputActionType.Button);
            if (!string.IsNullOrEmpty(keyboardBinding)) _toggle.AddBinding(keyboardBinding);
            if (!string.IsNullOrEmpty(gamepadBinding)) _toggle.AddBinding(gamepadBinding);

            if (clickOn != null || clickOff != null)
            {
                _audio = gameObject.AddComponent<AudioSource>();
                _audio.playOnAwake = false;
                _audio.spatialBlend = 0f;
            }
        }

        private void OnEnable() { _toggle.Enable(); }
        private void OnDisable() { _toggle.Disable(); }

        private void Start()
        {
            if (fpsPlayer == null) fpsPlayer = FindFirstObjectByType<FPSPlayer>();
            if (fpsPlayer == null)
            {
                Debug.LogError("BodycamFlashlight: FPSPlayer not found.", this);
                enabled = false;
                return;
            }

            if (startOn) { _isOn = true; _power = 1f; }
        }

        public void SetOn(bool on)
        {
            if (on == _isOn) return;
            if (on && batteryMinutes > 0f && _battery <= 0f) return;

            _isOn = on;
            if (on) _startup = startupFlickerTime;

            if (_audio != null)
            {
                var clip = on ? clickOn : clickOff;
                if (clip != null) _audio.PlayOneShot(clip, clickVolume);
            }
        }

        public void Recharge(float amount01)
        {
            _battery = Mathf.Clamp01(_battery + amount01);
        }

        private Entry Resolve(FPSWeapon weapon)
        {
            if (_entries.TryGetValue(weapon, out var existing)) return existing;

            // 1) A light authored by you in the weapon prefab.
            foreach (var l in weapon.GetComponentsInChildren<Light>(true))
            {
                if (l.name.IndexOf("flashlight", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var authored = new Entry { light = l, baseIntensity = l.intensity, authored = true };
                    l.enabled = false;
                    _entries[weapon] = authored;
                    return authored;
                }
            }

            // 2) Create one at runtime.
            var go = new GameObject("Flashlight");
            go.transform.SetParent(weapon.aimPoint != null ? weapon.aimPoint : weapon.transform, false);
            var light = go.AddComponent<Light>();
            light.type = LightType.Spot;
            light.enabled = false;

            var entry = new Entry { light = light, baseIntensity = intensity, authored = false };
            _entries[weapon] = entry;
            return entry;
        }

        private void ApplyRuntimeSettings(Entry e)
        {
            if (e.authored) return;

            // Re-applied every frame so you can tune values live in Play mode.
            var t = e.light.transform;
            t.localPosition = positionOffset;
            t.localRotation = Quaternion.Euler(rotationOffset);

            e.baseIntensity = intensity;
            e.light.color = color;
            e.light.range = range;
            e.light.spotAngle = spotAngle;
            e.light.innerSpotAngle = Mathf.Min(innerSpotAngle, spotAngle - 1f);
            e.light.shadows = shadows;
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            if (_toggle.WasPressedThisFrame()) SetOn(!_isOn);

            // --- Battery -----------------------------------------------------------------------
            bool hasBattery = batteryMinutes > 0f;
            if (hasBattery && _isOn)
            {
                _battery = Mathf.Max(0f, _battery - dt / (batteryMinutes * 60f));
                if (_battery <= 0f) SetOn(false);
            }
            if (!hasBattery) _battery = 1f;

            // --- Power fade --------------------------------------------------------------------
            _power = Mathf.MoveTowards(_power, _isOn ? 1f : 0f, dt / Mathf.Max(0.01f, fadeTime));

            // --- Flicker -----------------------------------------------------------------------
            float low = 0f;
            if (hasBattery && _battery < lowBatteryThreshold)
                low = 1f - _battery / Mathf.Max(0.0001f, lowBatteryThreshold);

            float amount = Mathf.Lerp(baseFlicker, lowBatteryFlicker, low);
            float flicker = 1f + (Mathf.PerlinNoise(Time.time * flickerSpeed, 0.37f) - 0.5f) * 2f * amount;

            if (_dropoutRemaining > 0f)
            {
                _dropoutRemaining -= dt;
                flicker *= 0.08f;
            }
            else if (low > 0f && UnityEngine.Random.value < dropoutsPerSecond * low * dt)
            {
                _dropoutRemaining = UnityEngine.Random.Range(0.04f, 0.18f);
            }

            if (_startup > 0f)
            {
                _startup -= dt;
                flicker *= Mathf.PerlinNoise(Time.time * 45f, 9.1f) > 0.35f ? 1f : 0.15f;
            }

            float dim = Mathf.Lerp(1f, lowBatteryDim, low);

            // --- Apply to the active weapon's light, make sure the others are off ---------------
            var active = fpsPlayer.GetActiveWeapon();
            if (active == null) return;

            var entry = Resolve(active);
            ApplyRuntimeSettings(entry);

            float final = entry.baseIntensity * _power * flicker * dim;
            entry.light.intensity = Mathf.Max(0f, final);
            entry.light.enabled = _power > 0.001f;

            foreach (var kv in _entries)
            {
                if (kv.Key != active && kv.Value.light != null) kv.Value.light.enabled = false;
            }
        }
    }
}
