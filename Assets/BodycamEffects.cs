using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Bodycam
{
    /// <summary>
    /// Post-processing look of a bodycam (URP). Creates its own global Volume at runtime,
    /// so there is no profile asset to set up. The look reacts to movement:
    /// sprinting adds grain + chromatic aberration, landings cause a brief lens "hit",
    /// and a slow noise simulates the auto-exposure hunting of cheap sensors.
    /// Put it on the same object as BodycamPlayerController.
    /// </summary>
    [DefaultExecutionOrder(50)]
    public class BodycamEffects : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private CharacterController body;
        [Tooltip("The rig camera. If empty, the first camera found is used.")]
        [SerializeField] private Camera cam;
        [Tooltip("Speed that counts as 'full sprint' for the reactive effects.")]
        [SerializeField] private float sprintReference = 5.6f;

        [Header("Lens")]
        [SerializeField] private bool overrideFov = true;
        [SerializeField] private float fieldOfView = 100f;
        [Tooltip("Barrel/fisheye amount. If it looks inverted (pincushion), flip the sign.")]
        [Range(-1f, 1f)] [SerializeField] private float distortion = 0.35f;
        [Tooltip("Lens distortion scale. Raise slightly (1.1-1.3) to hide the borders caused by barrel distortion.")]
        [Range(0.01f, 5f)] [SerializeField] private float distortionScale = 1.15f;

        [Header("Sensor look")]
        [Range(0f, 1f)] [SerializeField] private float chromaticBase = 0.25f;
        [Range(0f, 1f)] [SerializeField] private float chromaticSprint = 0.25f;
        [Range(0f, 1f)] [SerializeField] private float grainBase = 0.35f;
        [Range(0f, 1f)] [SerializeField] private float grainSprint = 0.25f;
        [Tooltip("Built-in URP vignette (round).")]
        [SerializeField] private bool useUrpVignette = true;
        [Range(0f, 1f)] [SerializeField] private float vignetteIntensity = 0.38f;
        [Tooltip("0 = colors untouched, 1 = black and white.")]
        [Range(0f, 1f)] [SerializeField] private float desaturation = 0f;
        [Tooltip("0 = untouched. Positive values brighten the highlights (sky) noticeably.")]
        [Range(-100f, 100f)] [SerializeField] private float contrast = 0f;

        [Header("Auto exposure hunting")]
        [Tooltip("0 = off (neutral brightness). Try 0.1-0.25 for exposure flicker.")]
        [SerializeField] private float exposureAmplitude = 0f;
        [SerializeField] private float exposureSpeed = 0.6f;

        [Header("Impacts")]
        [Tooltip("Minimum downward speed (m/s) on landing to trigger a lens hit.")]
        [SerializeField] private float landMinSpeed = 3f;
        [SerializeField] private float impactDecay = 3f;

        private Volume _volume;
        private VolumeProfile _profile;
        private LensDistortion _lens;
        private ChromaticAberration _chroma;
        private FilmGrain _grain;
        private Vignette _vignette;
        private ColorAdjustments _color;

        private float _impact;
        private float _lastVy;
        private bool _wasGrounded = true;

        private void Awake()
        {
            if (body == null) body = GetComponent<CharacterController>();
            if (cam == null) cam = FindFirstObjectByType<Camera>();

            BuildVolume();

            if (cam != null)
            {
                cam.GetUniversalAdditionalCameraData().renderPostProcessing = true;
                if (overrideFov) cam.fieldOfView = fieldOfView;
            }

            // Diagnostics: shows in the Console on Play so you can see why effects might not appear.
            string pipeline = GraphicsSettings.currentRenderPipeline != null
                ? GraphicsSettings.currentRenderPipeline.name
                : "NONE (Built-in: URP volumes will not work)";
            Debug.Log($"[BodycamEffects] Active. Camera: {(cam != null ? cam.name : "NOT FOUND")} | Pipeline: {pipeline}", this);

            if (cam != null)
            {
                var data = cam.GetUniversalAdditionalCameraData();
                if ((data.volumeLayerMask.value & (1 << _volume.gameObject.layer)) == 0)
                    Debug.LogWarning("[BodycamEffects] The camera's Volume Mask does not include the Volume's layer.", this);
                if (!data.renderPostProcessing)
                    Debug.LogWarning("[BodycamEffects] Post Processing is OFF on the camera.", this);
            }
        }

        private void BuildVolume()
        {
            _profile = ScriptableObject.CreateInstance<VolumeProfile>();

            _lens = _profile.Add<LensDistortion>(true);
            _lens.scale.value = distortionScale;

            _chroma = _profile.Add<ChromaticAberration>(true);
            _grain = _profile.Add<FilmGrain>(true);
            _grain.type.value = FilmGrainLookup.Medium3;
            _grain.response.value = 0.8f;

            _vignette = _profile.Add<Vignette>(true);
            _vignette.smoothness.value = 0.6f;
            _vignette.rounded.value = true;

            _color = _profile.Add<ColorAdjustments>(true);

            var go = new GameObject("BodycamVolume");
            go.transform.SetParent(transform, false);
            _volume = go.AddComponent<Volume>();
            _volume.isGlobal = true;
            _volume.priority = 100f;
            _volume.sharedProfile = _profile;
        }

        private void OnDestroy()
        {
            if (_profile != null) Destroy(_profile);
        }

        /// <summary>Call from anywhere (explosion, hit, etc.). 0..1</summary>
        public void Impact(float strength) => _impact = Mathf.Max(_impact, Mathf.Clamp01(strength));

        private void Update()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f || body == null) return;

            Vector3 v = body.velocity;
            float s01 = Mathf.Clamp01(new Vector2(v.x, v.z).magnitude / sprintReference);

            // Landing hit.
            bool grounded = body.isGrounded;
            if (grounded && !_wasGrounded && -_lastVy > landMinSpeed)
                Impact(Mathf.Clamp01(-_lastVy / 12f));
            if (!grounded) _lastVy = v.y;
            _wasGrounded = grounded;

            _impact = Mathf.MoveTowards(_impact, 0f, impactDecay * dt);

            _lens.intensity.value = distortion + _impact * 0.15f;
            _chroma.intensity.value = Mathf.Clamp01(chromaticBase + s01 * chromaticSprint + _impact * 0.5f);
            _grain.intensity.value = Mathf.Clamp01(grainBase + s01 * grainSprint);
            _vignette.intensity.value = useUrpVignette ? vignetteIntensity + s01 * 0.05f : 0f;

            float hunt = (Mathf.PerlinNoise(Time.time * exposureSpeed, 0.37f) - 0.5f) * 2f * exposureAmplitude;
            // Color grading only participates when something is non-neutral, so the base image stays untouched.
            _color.active = exposureAmplitude > 0f || desaturation > 0f
                            || !Mathf.Approximately(contrast, 0f) || _impact > 0f;
            _color.postExposure.value = hunt - _impact * 0.4f;
            _color.saturation.value = -desaturation * 100f;
            _color.contrast.value = contrast;
        }
    }
}