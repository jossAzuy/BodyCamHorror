using System.Collections.Generic;
using KINEMATION.FPSAnimationPack.Scripts.Player;
using KINEMATION.FPSAnimationPack.Scripts.Weapon;
using UnityEngine;
using UnityEngine.Rendering;

namespace Bodycam
{
    /// <summary>
    /// Shooting feedback for the FPS Animation Pack:
    ///  - Muzzle flash: particles (your prefab, or a procedural flash + sparks + smoke) plus a
    ///    short light pulse that lights up the room.
    ///  - Ejected casing: a small physics brass casing thrown out of the ejection port.
    ///  - Casing impact sound: played on each bounce (a procedural "tink" is generated if you
    ///    do not assign clips).
    ///
    /// The pack has no fire event, so a shot is detected when the active weapon's activeAmmo drops.
    /// Runs after everything else (order 300) so effects use the final weapon pose of the frame.
    /// Put it on the same object as BodycamPlayerController.
    /// </summary>
    [DefaultExecutionOrder(300)]
    public class BodycamShootFeedback : MonoBehaviour
    {
        [Header("References (auto-found if empty)")]
        [SerializeField] private FPSPlayer fpsPlayer;
        [SerializeField] private CharacterController body;

        [Header("Muzzle flash")]
        [SerializeField] private bool enableMuzzleFlash = true;
        [Tooltip("Optional: your own particle prefab. Empty = procedural flash, sparks and smoke.")]
        [SerializeField] private GameObject muzzleFlashPrefab;
        [Tooltip("Optional particle material. Empty = the render pipeline's default particle material.")]
        [SerializeField] private Material particleMaterial;
        [Tooltip("Where the effect appears, relative to the weapon's aimPoint. Ignored if the weapon has a child named 'Muzzle'.")]
        [SerializeField] private Vector3 muzzleOffset = new Vector3(0f, 0f, 0.22f);
        [SerializeField] private float flashSize = 1f;
        [SerializeField] private bool enableSparks = true;
        [Tooltip("Smoke uses an unlit material, keep it subtle in dark scenes.")]
        [SerializeField] private bool enableSmoke = true;

        [Header("Muzzle light")]
        [SerializeField] private Color flashLightColor = new Color(1f, 0.72f, 0.42f);
        [SerializeField] private float flashLightIntensity = 10f;
        [SerializeField] private float flashLightRange = 10f;
        [SerializeField] private float flashLightDuration = 0.06f;

        [Header("Casing ejection")]
        [SerializeField] private bool enableCasings = true;
        [Tooltip("Optional: your own casing prefab (needs a Collider). Empty = a small brass cylinder.")]
        [SerializeField] private GameObject casingPrefab;
        [Tooltip("Where casings leave the weapon, relative to the aimPoint. Ignored if the weapon has a child named 'Eject'.")]
        [SerializeField] private Vector3 ejectOffset = new Vector3(0.035f, 0.01f, 0.08f);
        [Tooltip("Ejection direction in the port's local space: x = right, y = up, z = forward.")]
        [SerializeField] private Vector3 ejectDirection = new Vector3(1f, 0.9f, -0.25f);
        [SerializeField] private Vector2 ejectSpeed = new Vector2(1.8f, 3f);
        [Tooltip("Random cone spread in degrees.")]
        [SerializeField] private float ejectSpread = 12f;
        [SerializeField] private float spinSpeed = 25f;
        [Tooltip("Diameter and length in meters. Slightly exaggerated so it is visible.")]
        [SerializeField] private Vector2 casingSize = new Vector2(0.0105f, 0.022f);
        [SerializeField] private Color casingColor = new Color(0.85f, 0.62f, 0.25f);
        [SerializeField] private int casingPoolSize = 40;
        [SerializeField] private float casingLifetime = 25f;
        [SerializeField, Range(0f, 1f)] private float inheritPlayerVelocity = 0.8f;
        [Tooltip("Weapons whose name contains any of these do not eject on fire (bolt-action, pump, launchers...).")]
        [SerializeField] private string[] noEjectionNameFilters = { "RPG", "Kar98", "Drake", "KXG" };

        [Header("Casing impact sound")]
        [Tooltip("Optional. Empty = a procedural brass 'tink' is generated.")]
        [SerializeField] private AudioClip[] casingImpactClips;
        [SerializeField, Range(0f, 1f)] private float casingVolume = 0.5f;
        [SerializeField] private Vector2 casingPitchRange = new Vector2(0.9f, 1.1f);
        [Tooltip("Ignore impacts slower than this (m/s) so resting casings stay quiet.")]
        [SerializeField] private float minImpactSpeed = 0.6f;
        [SerializeField] private float casingMaxDistance = 25f;

        [Header("Debug")]
        [Tooltip("Draws the muzzle (red) and ejection port (cyan) in the Scene/Game view while playing.")]
        [SerializeField] private bool showGizmos = true;

        /// <summary>Raised once per detected shot. Useful for noise, AI, UI...</summary>
        public event System.Action<FPSWeapon> ShotFired;

        public float MinImpactSpeed => minImpactSpeed;

        private class Entry
        {
            public Transform muzzle, eject;
            public bool muzzleAuthored, ejectAuthored, noEject;
            public ParticleSystem flash, sparks, smoke;
            public ParticleSystem[] prefabSystems;
            public Light flashLight;
            public float flashTimer;
        }

        private readonly Dictionary<FPSWeapon, Entry> _entries = new Dictionary<FPSWeapon, Entry>();
        private FPSWeapon _tracked;
        private int _lastAmmo;

        private readonly List<BodycamCasing> _casings = new List<BodycamCasing>();
        private int _nextCasing;
        private Transform _casingRoot;
        private Material _casingMaterial;

        private AudioSource[] _audioPool;
        private int _audioNext;
        private AudioClip[] _clips;

        private void Awake()
        {
            if (body == null) body = GetComponent<CharacterController>();

            _casingRoot = new GameObject("BodycamCasings").transform;

            _audioPool = new AudioSource[8];
            for (int i = 0; i < _audioPool.Length; i++)
            {
                var go = new GameObject("CasingAudio" + i);
                go.transform.SetParent(transform, false);
                var src = go.AddComponent<AudioSource>();
                src.playOnAwake = false;
                src.spatialBlend = 1f;
                src.dopplerLevel = 0f;
                src.minDistance = 0.6f;
                src.maxDistance = casingMaxDistance;
                _audioPool[i] = src;
            }

            _clips = casingImpactClips != null && casingImpactClips.Length > 0
                ? casingImpactClips
                : new[] { MakeCasingClip(3400f, 1), MakeCasingClip(3900f, 2), MakeCasingClip(4500f, 3) };
        }

        private void Start()
        {
            if (fpsPlayer == null) fpsPlayer = FindFirstObjectByType<FPSPlayer>();
            if (fpsPlayer == null)
            {
                Debug.LogError("BodycamShootFeedback: FPSPlayer not found.", this);
                enabled = false;
            }
        }

        private void OnDestroy()
        {
            if (_casingRoot != null) Destroy(_casingRoot.gameObject);
        }

        // =====================================================================================
        // Main loop
        // =====================================================================================

        private void LateUpdate()
        {
            var weapon = fpsPlayer.GetActiveWeapon();
            if (weapon != null)
            {
                var entry = Resolve(weapon);
                ApplyAnchors(entry);

                if (weapon != _tracked)
                {
                    _tracked = weapon;
                    _lastAmmo = weapon.activeAmmo;
                }

                int fired = _lastAmmo - weapon.activeAmmo;
                _lastAmmo = weapon.activeAmmo;

                // fired > 3 means something else changed the ammo (not a normal shot): ignore it.
                if (fired > 0 && fired <= 3)
                {
                    for (int i = 0; i < fired; i++) OnShot(weapon, entry);
                }
            }

            UpdateFlashLights(Time.deltaTime);
            RecycleOldCasings();
        }

        private void OnShot(FPSWeapon weapon, Entry e)
        {
            if (enableMuzzleFlash) PlayMuzzleFlash(e);
            if (enableCasings && !e.noEject) EjectCasing(e);
            ShotFired?.Invoke(weapon);
        }

        // =====================================================================================
        // Per-weapon setup
        // =====================================================================================

        private static Transform FindByName(Transform root, string fragment)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name.IndexOf(fragment, System.StringComparison.OrdinalIgnoreCase) >= 0) return t;
            }
            return null;
        }

        private Entry Resolve(FPSWeapon weapon)
        {
            if (_entries.TryGetValue(weapon, out var existing)) return existing;

            var e = new Entry();
            Transform parent = weapon.aimPoint != null ? weapon.aimPoint : weapon.transform;

            // Muzzle anchor: your own child named "Muzzle", otherwise a runtime one.
            e.muzzle = FindByName(weapon.transform, "muzzle");
            e.muzzleAuthored = e.muzzle != null;
            if (e.muzzle == null)
            {
                e.muzzle = new GameObject("BodycamMuzzle").transform;
                e.muzzle.SetParent(parent, false);
            }

            // Ejection port anchor: your own child named "Eject...", otherwise a runtime one.
            e.eject = FindByName(weapon.transform, "eject");
            e.ejectAuthored = e.eject != null;
            if (e.eject == null)
            {
                e.eject = new GameObject("BodycamEjectionPort").transform;
                e.eject.SetParent(parent, false);
            }

            string lowerName = weapon.name.ToLowerInvariant();
            foreach (var f in noEjectionNameFilters)
            {
                if (!string.IsNullOrEmpty(f) && lowerName.Contains(f.ToLowerInvariant())) { e.noEject = true; break; }
            }

            ApplyAnchors(e);
            BuildMuzzleFx(e);
            _entries[weapon] = e;
            return e;
        }

        /// <summary>Runtime anchors are repositioned every frame so offsets can be tuned live.</summary>
        private void ApplyAnchors(Entry e)
        {
            if (!e.muzzleAuthored)
            {
                e.muzzle.localPosition = muzzleOffset;
                e.muzzle.localRotation = Quaternion.identity;
            }
            if (!e.ejectAuthored)
            {
                e.eject.localPosition = ejectOffset;
                e.eject.localRotation = Quaternion.identity;
            }
        }

        // =====================================================================================
        // Muzzle flash
        // =====================================================================================

        private Material GetParticleMaterial()
        {
            if (particleMaterial != null) return particleMaterial;
            var rp = GraphicsSettings.currentRenderPipeline;
            return rp != null ? rp.defaultParticleMaterial : null;
        }

        private void BuildMuzzleFx(Entry e)
        {
            // Light pulse (very effective in dark scenes).
            var lightGo = new GameObject("BodycamMuzzleLight");
            lightGo.transform.SetParent(e.muzzle, false);
            lightGo.transform.localPosition = new Vector3(0f, 0f, 0.04f);
            e.flashLight = lightGo.AddComponent<Light>();
            e.flashLight.type = LightType.Point;
            e.flashLight.shadows = LightShadows.None;
            e.flashLight.intensity = 0f;
            e.flashLight.enabled = false;

            // Your own particles.
            if (muzzleFlashPrefab != null)
            {
                var instance = Instantiate(muzzleFlashPrefab, e.muzzle);
                instance.transform.localPosition = Vector3.zero;
                instance.transform.localRotation = Quaternion.identity;
                e.prefabSystems = instance.GetComponentsInChildren<ParticleSystem>(true);
                foreach (var ps in e.prefabSystems) ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                return;
            }

            // Procedural particles.
            var mat = GetParticleMaterial();
            if (mat == null)
            {
                Debug.LogWarning("BodycamShootFeedback: no particle material available. " +
                                 "Assign 'Particle Material' or a 'Muzzle Flash Prefab'. The light pulse still works.", this);
                return;
            }

            // Flash: 1-2 bright billboards for a few frames.
            e.flash = MakeParticleSystem("Flash", e.muzzle, mat, ParticleSystemRenderMode.Billboard);
            var fm = e.flash.main;
            fm.startLifetime = new ParticleSystem.MinMaxCurve(0.04f, 0.07f);
            fm.startSpeed = 0f;
            fm.startSize = new ParticleSystem.MinMaxCurve(0.12f * flashSize, 0.2f * flashSize);
            fm.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            fm.startColor = new Color(1f, 0.82f, 0.5f, 1f);
            fm.maxParticles = 8;
            var fs = e.flash.shape; fs.enabled = false;
            FadeOut(e.flash);
            Shrink(e.flash, 1f, 0.35f);

            // Sparks: small fast stretched particles in a narrow cone along the barrel.
            if (enableSparks)
            {
                e.sparks = MakeParticleSystem("Sparks", e.muzzle, mat, ParticleSystemRenderMode.Stretch);
                var sm = e.sparks.main;
                sm.simulationSpace = ParticleSystemSimulationSpace.World;
                sm.startLifetime = new ParticleSystem.MinMaxCurve(0.1f, 0.22f);
                sm.startSpeed = new ParticleSystem.MinMaxCurve(4f, 9f);
                sm.startSize = new ParticleSystem.MinMaxCurve(0.012f, 0.022f);
                sm.startColor = new Color(1f, 0.7f, 0.3f, 1f);
                sm.gravityModifier = 0.3f;
                sm.maxParticles = 30;
                var ss = e.sparks.shape;
                ss.enabled = true;
                ss.shapeType = ParticleSystemShapeType.Cone;
                ss.angle = 14f;
                ss.radius = 0.004f;
                FadeOut(e.sparks);
                var sr = e.sparks.GetComponent<ParticleSystemRenderer>();
                sr.lengthScale = 2f;
                sr.velocityScale = 0.04f;
            }

            // Smoke: a faint puff that drifts up and fades.
            if (enableSmoke)
            {
                e.smoke = MakeParticleSystem("Smoke", e.muzzle, mat, ParticleSystemRenderMode.Billboard);
                var mm = e.smoke.main;
                mm.simulationSpace = ParticleSystemSimulationSpace.World;
                mm.startLifetime = new ParticleSystem.MinMaxCurve(0.8f, 1.4f);
                mm.startSpeed = new ParticleSystem.MinMaxCurve(0.1f, 0.3f);
                mm.startSize = new ParticleSystem.MinMaxCurve(0.04f, 0.07f);
                mm.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
                mm.startColor = new Color(0.35f, 0.35f, 0.35f, 0.18f);
                mm.gravityModifier = -0.02f;
                mm.maxParticles = 12;
                var ms = e.smoke.shape;
                ms.enabled = true;
                ms.shapeType = ParticleSystemShapeType.Cone;
                ms.angle = 20f;
                ms.radius = 0.004f;
                FadeOut(e.smoke);
                Shrink(e.smoke, 0.5f, 2.5f);
            }
        }

        private static ParticleSystem MakeParticleSystem(string name, Transform parent, Material mat, ParticleSystemRenderMode mode)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);

            var ps = go.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.loop = true;
            main.playOnAwake = true;

            // Nothing is emitted automatically: each shot calls Emit().
            var emission = ps.emission;
            emission.rateOverTime = 0f;

            var rend = go.GetComponent<ParticleSystemRenderer>();
            rend.sharedMaterial = mat;
            rend.renderMode = mode;
            rend.shadowCastingMode = ShadowCastingMode.Off;
            rend.receiveShadows = false;

            ps.Play();
            return ps;
        }

        private static void FadeOut(ParticleSystem ps)
        {
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) });
            col.color = new ParticleSystem.MinMaxGradient(g);
        }

        private static void Shrink(ParticleSystem ps, float from, float to)
        {
            var sol = ps.sizeOverLifetime;
            sol.enabled = true;
            sol.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, from, 1f, to));
        }

        private void PlayMuzzleFlash(Entry e)
        {
            if (e.prefabSystems != null)
            {
                foreach (var ps in e.prefabSystems)
                {
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    ps.Play(true);
                }
            }

            if (e.flash != null) e.flash.Emit(Random.Range(1, 3));
            if (e.sparks != null) e.sparks.Emit(Random.Range(5, 9));
            if (e.smoke != null) e.smoke.Emit(1);

            if (e.flashLight != null)
            {
                e.flashLight.color = flashLightColor;
                e.flashLight.range = flashLightRange;
                e.flashTimer = flashLightDuration;
            }
        }

        private void UpdateFlashLights(float dt)
        {
            foreach (var e in _entries.Values)
            {
                if (e.flashLight == null) continue;

                if (e.flashTimer > 0f)
                {
                    e.flashTimer -= dt;
                    float k = Mathf.Clamp01(e.flashTimer / Mathf.Max(0.001f, flashLightDuration));
                    e.flashLight.intensity = flashLightIntensity * k;
                    e.flashLight.enabled = true;
                }
                else if (e.flashLight.enabled)
                {
                    e.flashLight.enabled = false;
                }
            }
        }

        // =====================================================================================
        // Casings
        // =====================================================================================

        private Material GetCasingMaterial(Material source)
        {
            if (_casingMaterial == null)
            {
                _casingMaterial = new Material(source);
                _casingMaterial.color = casingColor;
                if (_casingMaterial.HasProperty("_BaseColor")) _casingMaterial.SetColor("_BaseColor", casingColor);
                if (_casingMaterial.HasProperty("_Metallic")) _casingMaterial.SetFloat("_Metallic", 0.9f);
                if (_casingMaterial.HasProperty("_Smoothness")) _casingMaterial.SetFloat("_Smoothness", 0.65f);
            }
            return _casingMaterial;
        }

        private BodycamCasing CreateCasing()
        {
            GameObject go;
            if (casingPrefab != null)
            {
                go = Instantiate(casingPrefab, _casingRoot);
            }
            else
            {
                go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                go.transform.SetParent(_casingRoot, false);
                // The built-in cylinder is 1 wide and 2 tall.
                go.transform.localScale = new Vector3(casingSize.x, casingSize.y * 0.5f, casingSize.x);

                var r = go.GetComponent<Renderer>();
                r.sharedMaterial = GetCasingMaterial(r.sharedMaterial);
                r.shadowCastingMode = ShadowCastingMode.Off;
            }

            go.name = "Casing";
            go.layer = 2; // Ignore Raycast: casings never block gameplay raycasts.

            var col = go.GetComponent<Collider>();
            if (col == null)
            {
                Debug.LogWarning("BodycamShootFeedback: the casing prefab has no Collider, it will fall through the floor.", go);
            }
            else
            {
                BodycamCasing.ApplyBouncyMaterial(col);
                if (body != null) Physics.IgnoreCollision(col, body);
            }

            var rb = go.GetComponent<Rigidbody>();
            if (rb == null) rb = go.AddComponent<Rigidbody>();
            rb.mass = 0.004f;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.interpolation = RigidbodyInterpolation.Interpolate;

            var casing = go.GetComponent<BodycamCasing>();
            if (casing == null) casing = go.AddComponent<BodycamCasing>();
            casing.Body = rb;
            casing.Owner = this;

            go.SetActive(false);
            return casing;
        }

        private BodycamCasing GetCasing()
        {
            if (_casings.Count < casingPoolSize)
            {
                var created = CreateCasing();
                _casings.Add(created);
                return created;
            }

            // Pool full: recycle the oldest one.
            var reused = _casings[_nextCasing];
            _nextCasing = (_nextCasing + 1) % _casings.Count;
            return reused;
        }

        private void EjectCasing(Entry e)
        {
            var casing = GetCasing();

            Quaternion spread = Quaternion.Euler(
                Random.Range(-ejectSpread, ejectSpread),
                Random.Range(-ejectSpread, ejectSpread), 0f);
            Vector3 dir = e.eject.TransformDirection(spread * ejectDirection.normalized);

            Vector3 velocity = dir * Random.Range(ejectSpeed.x, ejectSpeed.y);
            if (body != null) velocity += body.velocity * inheritPlayerVelocity;

            // Reposition while inactive so the rigidbody starts clean.
            casing.gameObject.SetActive(false);
            casing.transform.SetPositionAndRotation(e.eject.position, Random.rotation);
            casing.gameObject.SetActive(true);
            casing.Launch(velocity, Random.insideUnitSphere * spinSpeed);
        }

        private void RecycleOldCasings()
        {
            for (int i = 0; i < _casings.Count; i++)
            {
                var c = _casings[i];
                if (c.gameObject.activeSelf && Time.time - c.SpawnTime > casingLifetime)
                    c.gameObject.SetActive(false);
            }
        }

        // =====================================================================================
        // Casing impact sound
        // =====================================================================================

        /// <summary>Called by BodycamCasing on each collision. Later bounces are quieter.</summary>
        public void PlayCasingImpact(Vector3 position, float impactSpeed, int bounceIndex)
        {
            if (_clips == null || _clips.Length == 0) return;

            float volume = casingVolume * Mathf.Clamp01(impactSpeed / 3f) / (1f + bounceIndex * 0.6f);
            if (volume < 0.01f) return;

            var src = _audioPool[_audioNext];
            _audioNext = (_audioNext + 1) % _audioPool.Length;

            src.transform.position = position;
            src.maxDistance = casingMaxDistance;
            src.pitch = Random.Range(casingPitchRange.x, casingPitchRange.y);
            src.PlayOneShot(_clips[Random.Range(0, _clips.Length)], volume);
        }

        /// <summary>
        /// Simulated brass "tink": a few decaying partials plus a tiny noise click.
        /// Used when no impact clips are assigned.
        /// </summary>
        private static AudioClip MakeCasingClip(float baseFreq, int seed)
        {
            const int sampleRate = 44100;
            int n = (int)(sampleRate * 0.22f);
            var data = new float[n];
            var rng = new System.Random(seed);

            float[] freqs = { baseFreq, baseFreq * 1.62f, baseFreq * 2.41f };
            float[] amps = { 0.5f, 0.3f, 0.2f };
            float[] decays = { 28f, 38f, 55f };

            for (int i = 0; i < n; i++)
            {
                float t = i / (float)sampleRate;
                float s = 0f;
                for (int k = 0; k < freqs.Length; k++)
                    s += Mathf.Sin(2f * Mathf.PI * freqs[k] * t) * amps[k] * Mathf.Exp(-decays[k] * t);

                s += ((float)rng.NextDouble() * 2f - 1f) * 0.35f * Mathf.Exp(-400f * t);
                data[i] = Mathf.Clamp(s * 0.8f, -1f, 1f);
            }

            var clip = AudioClip.Create("BodycamCasingTink" + seed, n, 1, sampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }

        // =====================================================================================
        // Debug
        // =====================================================================================

        private void OnDrawGizmos()
        {
            if (!showGizmos || !Application.isPlaying || _tracked == null) return;
            if (!_entries.TryGetValue(_tracked, out var e)) return;

            Gizmos.color = Color.red;
            Gizmos.DrawWireSphere(e.muzzle.position, 0.01f);
            Gizmos.DrawRay(e.muzzle.position, e.muzzle.forward * 0.15f);

            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(e.eject.position, 0.01f);
            Gizmos.DrawRay(e.eject.position, e.eject.TransformDirection(ejectDirection.normalized) * 0.15f);
        }
    }
}
