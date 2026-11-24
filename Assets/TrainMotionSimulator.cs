using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

public enum TrainState { Cruise, Braking, Stopped, Accelerating }

/// <summary>
/// Simula el movimiento de un tren SIN mover nada realmente.
/// Capas: cámara, interior (asideros + luces) y estados del tren.
/// El audio vive en TrainAudioSimulator y se suscribe a los eventos de aquí.
///
/// Ejes: el eje Z de "wagon" debe apuntar en el sentido de la marcha,
/// X a la derecha, Y arriba. La cámara se mueve en los ejes del vagón,
/// no en los de la vista, así que el vagón se inclina igual aunque mires de lado.
/// </summary>
public class TrainMotionSimulator : MonoBehaviour
{
    [Header("Referencias")]
    [Tooltip("Transform del vagón (su eje Z = sentido de la marcha).")]
    public Transform wagon;
    [Tooltip("Objeto vacío entre el jugador y la cámara (rotación local identidad). Se mueve éste, no la cámara.")]
    public Transform cameraShakeRoot;
    [Tooltip("Asideros colgantes. Su pivote debe estar ARRIBA (donde cuelgan).")]
    public List<Transform> handrails = new List<Transform>();
    public List<Light> lights = new List<Light>();
    [Tooltip("Opcional: renders con emisión (propiedad _EmissionColor) que parpadean con las luces.")]
    public List<Renderer> emissiveRenderers = new List<Renderer>();

    [Header("Estados del tren")]
    public TrainState initialState = TrainState.Cruise;
    [Tooltip("Cicla solo: crucero > frenado > parada > aceleración.")]
    public bool autoCycle = true;
    public float cruiseSpeed = 18f;          // m/s
    public float acceleration = 1.1f;        // m/s²
    public float braking = 1.6f;             // m/s²
    public Vector2 cruiseTime = new Vector2(30f, 55f);
    public Vector2 stopTime = new Vector2(8f, 14f);

    [Header("Vías")]
    [Tooltip("Largo de cada riel; cada junta produce un golpeteo.")]
    public float railLength = 12f;
    [Tooltip("Distancia entre ejes de un bogie: genera el 'ta-tac' doble.")]
    public float bogieSpacing = 2.4f;

    [Header("Intensidades")]
    [Range(0f, 2f)] public float intensity = 1f;
    [Range(0f, 2f)] public float jointStrength = 1f;
    [Range(0f, 2f)] public float joltStrength = 1f;
    [Tooltip("Segundos entre sacudidas fuertes aleatorias (solo en marcha).")]
    public Vector2 joltInterval = new Vector2(14f, 32f);

    [Header("Curvas")]
    public Vector2 curveGap = new Vector2(10f, 25f);
    public Vector2 curveDuration = new Vector2(4f, 8f);
    public float maxLateralAccel = 1.6f;
    [Tooltip("Actívalo si el vagón se inclina hacia el lado contrario al esperado.")]
    public bool invertRoll = false;

    [Header("Cámara")]
    public float swayFrequency = 0.9f;
    public float swayRollDeg = 0.35f;
    public float swayPosition = 0.004f;
    public float vibrationPosition = 0.0015f;
    public float vibrationRollDeg = 0.06f;
    public float vibrationFrequency = 22f;
    [Tooltip("Grados de cabeceo por m/s² de aceleración longitudinal.")]
    public float pitchPerAccel = 0.7f;
    [Tooltip("Grados de balanceo por m/s² de aceleración lateral.")]
    public float rollPerAccel = 0.8f;
    [Tooltip("Metros de desplazamiento del cuerpo por m/s².")]
    public float leanPerAccel = 0.006f;
    public float shockFrequency = 5f;
    [Range(0.05f, 1f)] public float shockDamping = 0.3f;

    [Header("Asideros")]
    public float handrailLength = 0.45f;
    [Range(0.02f, 0.6f)] public float handrailDamping = 0.18f;
    public float handrailSwingGain = 1.2f;

    [Header("Luces")]
    public float flickerStep = 0.05f;
    public float flickerDuration = 0.6f;

    [Header("Depuración")]
    [Tooltip("Teclas 1-4 cambian de estado, J = sacudida, C = curva (Input Manager clásico).")]
    public bool debugKeys = false;

    // ---- API pública ----
    public TrainState State { get; private set; }
    public float Speed => speed;
    public float Speed01 => cruiseSpeed > 0f ? speed / cruiseSpeed : 0f;
    public float LongAccel => longAccel;
    /// <summary>0..1: intensidad para el chirrido de curva (depende de la velocidad).</summary>
    public float CurveSqueal => Mathf.Abs(curveAmount) * Mathf.Clamp01(Speed01 * 1.2f);

    public event System.Action<float> OnJointHit;   // intensidad 0..1
    public event System.Action<float> OnJolt;       // intensidad 0..1.5
    public event System.Action<TrainState, TrainState> OnStateChanged;

    // ---- estado interno ----
    float speed, longAccel, latAccel;
    float stateTimer;

    // curvas
    bool inCurve; float curveTimer, curveGapTimer, curveTarget, curveAmount, curveVel;

    // juntas y sacudidas
    float jointDist, pendingSecond = -1f, jointSide = 1f, joltTimer;

    // resorte de la cámara (espacio del vagón)
    Vector3 shockPos, shockPosVel, shockRot, shockRotVel;

    // asideros
    Quaternion[] hrInit; Vector2[] hrAng, hrVel; float[] hrGain, hrFreq;

    // luces
    float[] lightBase, lightLevel; Color[] emisBase; float globalLevel = 1f;
    float flickerTimer, flickerStepTimer;
    MaterialPropertyBlock mpb;

    // cámara
    Quaternion camInitRot; Vector3 camInitPos; float seed;

    void Awake()
    {
        seed = Random.value * 100f;
        mpb = new MaterialPropertyBlock();

        hrInit = new Quaternion[handrails.Count];
        hrAng = new Vector2[handrails.Count];
        hrVel = new Vector2[handrails.Count];
        hrGain = new float[handrails.Count];
        hrFreq = new float[handrails.Count];
        for (int i = 0; i < handrails.Count; i++)
        {
            if (handrails[i]) hrInit[i] = handrails[i].localRotation;
            hrGain[i] = Random.Range(0.8f, 1.2f);
            hrFreq[i] = Random.Range(0.85f, 1.15f);
        }

        lightBase = new float[lights.Count];
        lightLevel = new float[lights.Count];
        for (int i = 0; i < lights.Count; i++)
        {
            lightBase[i] = lights[i] ? lights[i].intensity : 0f;
            lightLevel[i] = 1f;
        }
        emisBase = new Color[emissiveRenderers.Count];
        for (int i = 0; i < emissiveRenderers.Count; i++)
        {
            var r = emissiveRenderers[i];
            if (r && r.sharedMaterial && r.sharedMaterial.HasProperty("_EmissionColor"))
                emisBase[i] = r.sharedMaterial.GetColor("_EmissionColor");
        }

        if (cameraShakeRoot)
        {
            camInitRot = cameraShakeRoot.localRotation;
            camInitPos = cameraShakeRoot.localPosition;
        }
    }

    void Start()
    {
        if (!wagon) wagon = transform;
        State = initialState;
        speed = initialState == TrainState.Cruise || initialState == TrainState.Braking ? cruiseSpeed : 0f;
        stateTimer = Random.Range(cruiseTime.x, cruiseTime.y);
        if (initialState == TrainState.Stopped) stateTimer = Random.Range(stopTime.x, stopTime.y);
        curveGapTimer = Random.Range(curveGap.x, curveGap.y);
        joltTimer = Random.Range(joltInterval.x, joltInterval.y);
    }

    void Update()
    {
        float dt = Mathf.Min(Time.deltaTime, 0.05f);
        if (dt <= 0f) return;

        HandleDebugKeys();
        UpdateTrainState(dt);
        UpdateCurves(dt);
        UpdateJoints(dt);
        UpdateJolts(dt);
        UpdateShockSpring(dt);
        UpdateHandrails(dt);
        UpdateLights(dt);
    }

    void LateUpdate() => ApplyCamera();

    // =====================================================================
    // Estados
    // =====================================================================
    public void SetState(TrainState s)
    {
        var old = State;
        if (old == s) return;
        State = s;
        switch (s)
        {
            case TrainState.Cruise:
                stateTimer = Random.Range(cruiseTime.x, cruiseTime.y);
                break;
            case TrainState.Stopped:
                stateTimer = Random.Range(stopTime.x, stopTime.y);
                if (old == TrainState.Braking) TriggerJolt(0.35f);   // tirón final al detenerse
                break;
            case TrainState.Accelerating:
                if (old == TrainState.Stopped) TriggerJolt(0.3f);    // tirón al arrancar
                break;
        }
        OnStateChanged?.Invoke(old, s);
    }

    void UpdateTrainState(float dt)
    {
        float prev = speed;
        switch (State)
        {
            case TrainState.Cruise:
                speed = Mathf.MoveTowards(speed, cruiseSpeed, acceleration * dt);
                if (autoCycle) { stateTimer -= dt; if (stateTimer <= 0f) SetState(TrainState.Braking); }
                break;
            case TrainState.Braking:
                speed = Mathf.MoveTowards(speed, 0f, braking * dt);
                if (speed <= 0.01f) { speed = 0f; SetState(TrainState.Stopped); }
                break;
            case TrainState.Stopped:
                speed = 0f;
                if (autoCycle) { stateTimer -= dt; if (stateTimer <= 0f) SetState(TrainState.Accelerating); }
                break;
            case TrainState.Accelerating:
                speed = Mathf.MoveTowards(speed, cruiseSpeed, acceleration * dt);
                if (speed >= cruiseSpeed - 0.01f) SetState(TrainState.Cruise);
                break;
        }
        float raw = (speed - prev) / dt;
        longAccel = Mathf.Lerp(longAccel, raw, 1f - Mathf.Exp(-dt / 0.25f)); // suaviza la inercia
    }

    // =====================================================================
    // Curvas
    // =====================================================================
    public void TriggerCurve(float direction, float duration)
    {
        inCurve = true;
        curveTarget = Mathf.Clamp(direction, -1f, 1f); // +1 = curva a la derecha
        curveTimer = duration;
    }

    void UpdateCurves(float dt)
    {
        bool moving = speed > cruiseSpeed * 0.3f;
        if (moving)
        {
            if (inCurve)
            {
                curveTimer -= dt;
                if (curveTimer <= 0f)
                {
                    inCurve = false; curveTarget = 0f;
                    curveGapTimer = Random.Range(curveGap.x, curveGap.y);
                }
            }
            else
            {
                curveGapTimer -= dt;
                if (curveGapTimer <= 0f)
                    TriggerCurve((Random.value < 0.5f ? -1f : 1f) * Random.Range(0.5f, 1f),
                                 Random.Range(curveDuration.x, curveDuration.y));
            }
        }
        else { inCurve = false; curveTarget = 0f; }

        curveAmount = Mathf.SmoothDamp(curveAmount, curveTarget, ref curveVel, 1.2f);
        // aceleración lateral del tren (+ = hacia la derecha del vagón)
        latAccel = curveAmount * maxLateralAccel * Speed01 * Speed01;
    }

    // =====================================================================
    // Juntas de riel y sacudidas
    // =====================================================================
    void UpdateJoints(float dt)
    {
        if (speed < 0.8f) { jointDist = 0f; pendingSecond = -1f; return; }

        jointDist += speed * dt;
        if (jointDist >= railLength)
        {
            jointDist -= railLength;
            FireJoint(1f);
            pendingSecond = bogieSpacing / speed;   // segundo eje del bogie
        }
        if (pendingSecond >= 0f)
        {
            pendingSecond -= dt;
            if (pendingSecond <= 0f) { FireJoint(0.8f); pendingSecond = -1f; }
        }
    }

    void FireJoint(float scale)
    {
        float k = Mathf.Clamp01(Speed01) * scale * jointStrength;
        jointSide = -jointSide;
        shockPosVel += new Vector3(0f, -0.5f * k, 0f);
        shockRotVel += new Vector3(12f * k, 0f, jointSide * 10f * k);
        KickHandrails(0.25f * k);
        OnJointHit?.Invoke(k);
    }

    public void TriggerJolt(float strength = 1f)
    {
        float k = strength * joltStrength;
        shockPosVel += new Vector3(Random.Range(-1f, 1f) * 0.7f, -1.4f, Random.Range(-0.4f, 0.4f)) * k;
        shockRotVel += new Vector3(Random.Range(-35f, 35f), Random.Range(-10f, 10f), Random.Range(-60f, 60f)) * k;
        KickHandrails(1.5f * k);
        if (strength >= 0.5f) flickerTimer = flickerDuration * Mathf.Clamp(strength, 0.5f, 1.5f);
        OnJolt?.Invoke(strength);
    }

    void UpdateJolts(float dt)
    {
        if (Speed01 < 0.4f) return;
        joltTimer -= dt;
        if (joltTimer <= 0f)
        {
            TriggerJolt(Random.Range(0.7f, 1.3f));
            joltTimer = Random.Range(joltInterval.x, joltInterval.y);
        }
    }

    void UpdateShockSpring(float dt)
    {
        float w = 2f * Mathf.PI * shockFrequency;
        float c = 2f * shockDamping * w;
        int steps = Mathf.CeilToInt(dt / 0.008f);
        float h = dt / steps;
        for (int i = 0; i < steps; i++)
        {
            shockPosVel += (-w * w * shockPos - c * shockPosVel) * h;
            shockPos += shockPosVel * h;
            shockRotVel += (-w * w * shockRot - c * shockRotVel) * h;
            shockRot += shockRotVel * h;
        }
    }

    // =====================================================================
    // Cámara (en ejes del vagón)
    // =====================================================================
    void ApplyCamera()
    {
        if (!cameraShakeRoot || !wagon) return;
        float t = Time.time;
        float sp = Mathf.Clamp01(Speed01);
        float vib = Mathf.Lerp(0.15f, 1f, sp);
        float rollSign = invertRoll ? -1f : 1f;

        // balanceo lateral lento
        float sway = Mathf.Sin(t * swayFrequency * 2f * Mathf.PI) * 0.6f
                   + (Mathf.PerlinNoise(t * 0.35f, seed) - 0.5f) * 2f * 0.4f;
        float swayRoll = sway * swayRollDeg * sp;
        float swayX = Mathf.Sin(t * swayFrequency * 2f * Mathf.PI + 1f) * swayPosition * sp;

        // vibración de las vías
        float vy = (Mathf.PerlinNoise(t * vibrationFrequency, seed + 10f) - 0.5f) * 2f * vibrationPosition * vib;
        float vr = (Mathf.PerlinNoise(t * vibrationFrequency, seed + 20f) - 0.5f) * 2f * vibrationRollDeg * vib;
        float vp = (Mathf.PerlinNoise(t * vibrationFrequency, seed + 30f) - 0.5f) * 2f * vibrationRollDeg * 0.6f * vib;

        // inercia al frenar/acelerar y en curva
        float pitch = -longAccel * pitchPerAccel;                  // frenar = inclina hacia delante
        float roll = latAccel * rollPerAccel * rollSign;
        float leanZ = -longAccel * leanPerAccel;
        float leanX = -latAccel * leanPerAccel;

        Vector3 posOffset = (shockPos + new Vector3(swayX + leanX, vy, leanZ)) * intensity;
        Vector3 rotOffset = (shockRot + new Vector3(pitch + vp, 0f, roll + swayRoll + vr)) * intensity;

        Transform parent = cameraShakeRoot.parent;
        Quaternion wagonRot = wagon.rotation;
        Quaternion parentRot = parent ? parent.rotation : Quaternion.identity;

        // rotación aplicada en el espacio del vagón
        Quaternion d = wagonRot * Quaternion.Euler(rotOffset) * Quaternion.Inverse(wagonRot);
        cameraShakeRoot.localRotation = Quaternion.Inverse(parentRot) * d * parentRot * camInitRot;

        Vector3 worldOffset = wagon.TransformDirection(posOffset);
        cameraShakeRoot.localPosition = camInitPos +
            (parent ? parent.InverseTransformDirection(worldOffset) : worldOffset);
    }

    // =====================================================================
    // Asideros (péndulos que siguen la inercia del vagón)
    // =====================================================================
    void KickHandrails(float k)
    {
        for (int i = 0; i < hrVel.Length; i++)
            hrVel[i] += new Vector2(Random.Range(-1f, 1f), Random.Range(-1f, 1f)) * 0.5f * k;
    }

    void UpdateHandrails(float dt)
    {
        if (!wagon || handrails.Count == 0) return;
        const float g = 9.81f;
        float w0 = Mathf.Sqrt(g / Mathf.Max(0.1f, handrailLength));
        float targetPitch = Mathf.Clamp(longAccel / g * handrailSwingGain, -0.5f, 0.5f);
        float targetRoll = Mathf.Clamp(-latAccel / g * handrailSwingGain, -0.5f, 0.5f);
        Vector3 right = wagon.right, fwd = wagon.forward;

        for (int i = 0; i < handrails.Count; i++)
        {
            var h = handrails[i];
            if (!h) continue;
            float w = w0 * hrFreq[i];
            float c = 2f * handrailDamping * w;
            Vector2 target = new Vector2(targetPitch, targetRoll) * hrGain[i];
            Vector2 a = w * w * (target - hrAng[i]) - c * hrVel[i];
            hrVel[i] += a * dt;
            hrAng[i] += hrVel[i] * dt;
            hrAng[i].x = Mathf.Clamp(hrAng[i].x, -0.7f, 0.7f);
            hrAng[i].y = Mathf.Clamp(hrAng[i].y, -0.7f, 0.7f);

            Quaternion delta = Quaternion.AngleAxis(hrAng[i].x * Mathf.Rad2Deg, right) *
                               Quaternion.AngleAxis(hrAng[i].y * Mathf.Rad2Deg, fwd);
            Quaternion pr = h.parent ? h.parent.rotation : Quaternion.identity;
            h.localRotation = Quaternion.Inverse(pr) * delta * pr * hrInit[i];
        }
    }

    // =====================================================================
    // Luces
    // =====================================================================
    void UpdateLights(float dt)
    {
        bool flickering = flickerTimer > 0f;
        if (flickering)
        {
            flickerTimer -= dt;
            flickerStepTimer -= dt;
            if (flickerStepTimer <= 0f)
            {
                flickerStepTimer = flickerStep;
                float fade = Mathf.Clamp01(flickerTimer / flickerDuration);
                float chance = 0.55f * fade + 0.1f;
                for (int i = 0; i < lightLevel.Length; i++)
                    lightLevel[i] = Random.value < chance ? Random.Range(0.05f, 0.5f) : 1f;
                globalLevel = Random.value < chance ? Random.Range(0.05f, 0.5f) : 1f;
            }
        }
        else
        {
            for (int i = 0; i < lightLevel.Length; i++)
                lightLevel[i] = Mathf.MoveTowards(lightLevel[i], 1f, dt * 8f);
            globalLevel = Mathf.MoveTowards(globalLevel, 1f, dt * 8f);
        }

        bool dirty = flickering || globalLevel < 1f;
        for (int i = 0; i < lightLevel.Length && !dirty; i++) dirty = lightLevel[i] < 1f;
        if (!dirty && flickerTimer <= 0f)
        {
            // asegura el valor final exacto una vez
            for (int i = 0; i < lights.Count; i++) if (lights[i]) lights[i].intensity = lightBase[i];
            return;
        }

        for (int i = 0; i < lights.Count; i++)
            if (lights[i]) lights[i].intensity = lightBase[i] * lightLevel[i];

        for (int i = 0; i < emissiveRenderers.Count; i++)
        {
            var r = emissiveRenderers[i];
            if (!r) continue;
            r.GetPropertyBlock(mpb);
            mpb.SetColor("_EmissionColor", emisBase[i] * globalLevel);
            r.SetPropertyBlock(mpb);
        }
    }

    // =====================================================================
    void HandleDebugKeys()
    {
#if ENABLE_LEGACY_INPUT_MANAGER
        if (!debugKeys) return;
        if (Input.GetKeyDown(KeyCode.Alpha1)) SetState(TrainState.Cruise);
        if (Input.GetKeyDown(KeyCode.Alpha2)) SetState(TrainState.Braking);
        if (Input.GetKeyDown(KeyCode.Alpha3)) SetState(TrainState.Stopped);
        if (Input.GetKeyDown(KeyCode.Alpha4)) SetState(TrainState.Accelerating);
        if (Input.GetKeyDown(KeyCode.J)) TriggerJolt(1f);
        if (Input.GetKeyDown(KeyCode.C)) TriggerCurve(Random.value < 0.5f ? -1f : 1f, 6f);
#endif
    }
}
