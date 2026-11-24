using UnityEngine;
using UnityEngine.Audio;

/// <summary>
/// Audio del tren. Se suscribe a TrainMotionSimulator.
/// Si un clip queda vacío, se genera uno procedural: funciona sin assets.
/// Capas: retumbar de fondo (loop), golpeteo de juntas, sacudidas y chirrido (loop).
/// </summary>
public class TrainAudioSimulator : MonoBehaviour
{
    public TrainMotionSimulator motion;
    public AudioMixerGroup outputGroup;

    [Header("Clips opcionales (vacío = procedural)")]
    public AudioClip rumbleClip;
    public AudioClip jointClip;
    public AudioClip joltClip;
    public AudioClip squealClip;

    [Header("Volúmenes")]
    [Range(0f, 1f)] public float master = 1f;
    [Range(0f, 1f)] public float rumbleVolume = 0.6f;
    [Range(0f, 1f)] public float jointVolume = 0.7f;
    [Range(0f, 1f)] public float joltVolume = 0.9f;
    [Range(0f, 1f)] public float squealVolume = 0.35f;

    AudioSource rumbleSrc, squealSrc;
    AudioSource[] pool;
    int poolIndex;
    float rumbleVol, squealVol;
    System.Random rng;

    void Awake()
    {
        if (!motion) motion = GetComponent<TrainMotionSimulator>();
        rng = new System.Random(7341);
        int sr = AudioSettings.outputSampleRate;

        if (!rumbleClip) rumbleClip = BuildRumble(sr);
        if (!jointClip) jointClip = BuildClack(sr);
        if (!joltClip) joltClip = BuildJolt(sr);
        if (!squealClip) squealClip = BuildSqueal(sr);

        rumbleSrc = CreateSource(true); rumbleSrc.clip = rumbleClip;
        squealSrc = CreateSource(true); squealSrc.clip = squealClip;
        pool = new AudioSource[6];
        for (int i = 0; i < pool.Length; i++) pool[i] = CreateSource(false);
    }

    void Start()
    {
        rumbleSrc.Play();
        squealSrc.Play();
    }

    void OnEnable()
    {
        if (!motion) return;
        motion.OnJointHit += HandleJoint;
        motion.OnJolt += HandleJolt;
    }

    void OnDisable()
    {
        if (!motion) return;
        motion.OnJointHit -= HandleJoint;
        motion.OnJolt -= HandleJolt;
    }

    void Update()
    {
        if (!motion) return;
        float dt = Time.deltaTime;
        float s = Mathf.Clamp01(motion.Speed01);

        // retumbar
        float rt = master * rumbleVolume * Mathf.Lerp(0.05f, 1f, Mathf.Pow(s, 0.7f));
        rumbleVol = Mathf.Lerp(rumbleVol, rt, 1f - Mathf.Exp(-3f * dt));
        rumbleSrc.volume = rumbleVol;
        rumbleSrc.pitch = Mathf.Lerp(0.75f, 1.2f, s);

        // chirrido: curvas y, más suave, el final del frenado
        float sq = motion.CurveSqueal;
        if (motion.State == TrainState.Braking)
            sq = Mathf.Max(sq, Mathf.InverseLerp(0.3f, 0.05f, s) * 0.6f);
        float st = master * squealVolume * Mathf.Clamp01((sq - 0.2f) / 0.6f);
        squealVol = Mathf.Lerp(squealVol, st, 1f - Mathf.Exp(-5f * dt));
        squealSrc.volume = squealVol;
        squealSrc.pitch = Mathf.Lerp(0.75f, 1.15f, Mathf.Clamp01(s * 1.5f));
    }

    void HandleJoint(float k)
    {
        Play(jointClip, master * jointVolume * Mathf.Lerp(0.35f, 1f, Mathf.Clamp01(k)),
             1f + Random.Range(-0.08f, 0.08f));
    }

    void HandleJolt(float k)
    {
        Play(joltClip, master * joltVolume * Mathf.Clamp01(0.5f + k * 0.5f),
             Random.Range(0.92f, 1.05f));
    }

    void Play(AudioClip clip, float vol, float pitch)
    {
        if (!clip) return;
        var src = pool[poolIndex];
        poolIndex = (poolIndex + 1) % pool.Length;
        src.pitch = pitch;
        src.PlayOneShot(clip, Mathf.Clamp01(vol));
    }

    AudioSource CreateSource(bool loop)
    {
        var go = new GameObject("TrainAudio");
        go.transform.SetParent(transform, false);
        var s = go.AddComponent<AudioSource>();
        s.playOnAwake = false;
        s.loop = loop;
        s.spatialBlend = 0f;     // 2D: el jugador va dentro del vagón
        s.volume = 0f;
        s.outputAudioMixerGroup = outputGroup;
        return s;
    }

    // =====================================================================
    // Sonidos procedurales
    // =====================================================================
    float Noise() => (float)(rng.NextDouble() * 2.0 - 1.0);

    static void Normalize(float[] d, float peak)
    {
        float m = 0f;
        for (int i = 0; i < d.Length; i++) m = Mathf.Max(m, Mathf.Abs(d[i]));
        if (m < 1e-6f) return;
        float k = peak / m;
        for (int i = 0; i < d.Length; i++) d[i] *= k;
    }

    // Mezcla el final con el inicio para que el loop no haga "click".
    static float[] MakeLoop(float[] d, int loopLen)
    {
        int fade = d.Length - loopLen;
        var o = new float[loopLen];
        for (int i = 0; i < loopLen; i++)
        {
            if (i < fade)
            {
                float a = (float)i / fade;
                o[i] = d[i] * a + d[loopLen + i] * (1f - a);
            }
            else o[i] = d[i];
        }
        return o;
    }

    static AudioClip ToClip(string name, float[] d, int sr)
    {
        var c = AudioClip.Create(name, d.Length, 1, sr, false);
        c.SetData(d, 0);
        return c;
    }

    // Retumbar grave continuo: tonos graves + ruido filtrado.
    AudioClip BuildRumble(int sr)
    {
        int loop = sr * 3, fade = sr / 4;
        var d = new float[loop + fade];
        double[] freqs = { 31, 44, 58, 73, 97 };   // enteros: cierran el loop de 3 s
        float[] amps = { 1f, 0.8f, 0.6f, 0.45f, 0.3f };
        double[] ph = new double[freqs.Length];
        for (int k = 0; k < ph.Length; k++) ph[k] = rng.NextDouble() * System.Math.PI * 2;

        float lp1 = 0f, lp2 = 0f;
        for (int i = 0; i < d.Length; i++)
        {
            double t = (double)i / sr;
            float tone = 0f;
            for (int k = 0; k < freqs.Length; k++)
                tone += (float)System.Math.Sin(2 * System.Math.PI * freqs[k] * t + ph[k]) * amps[k];
            lp1 += (Noise() - lp1) * 0.04f;
            lp2 += (lp1 - lp2) * 0.08f;
            float mod = 1f + 0.2f * (float)System.Math.Sin(2 * System.Math.PI * t);
            d[i] = (tone * 0.5f + lp2 * 9f + Noise() * 0.03f) * mod;
        }
        var o = MakeLoop(d, loop);
        Normalize(o, 0.8f);
        return ToClip("Rumble_Proc", o, sr);
    }

    // Golpe seco metálico de una junta de riel.
    AudioClip BuildClack(int sr)
    {
        var d = new float[(int)(sr * 0.4f)];
        for (int i = 0; i < d.Length; i++)
        {
            float t = (float)i / sr;
            float thump = Mathf.Exp(-t * 55f) * Mathf.Sin(2f * Mathf.PI * 88f * t) * 0.9f;
            float metal = Mathf.Exp(-t * 30f) * (Mathf.Sin(2f * Mathf.PI * 640f * t) * 0.25f
                        + Mathf.Sin(2f * Mathf.PI * 1480f * t) * 0.18f
                        + Mathf.Sin(2f * Mathf.PI * 2310f * t) * 0.08f);
            float click = Mathf.Exp(-t * 260f) * Noise() * 0.8f;
            d[i] = thump + metal + click;
        }
        Normalize(d, 0.9f);
        return ToClip("Clack_Proc", d, sr);
    }

    // Sacudida fuerte: golpe grave + traqueteo.
    AudioClip BuildJolt(int sr)
    {
        var d = new float[(int)(sr * 1.2f)];
        float lp = 0f;
        for (int i = 0; i < d.Length; i++)
        {
            float t = (float)i / sr;
            lp += (Noise() - lp) * 0.25f;
            float thump = Mathf.Exp(-t * 6f) * Mathf.Sin(2f * Mathf.PI * 48f * t)
                        + Mathf.Exp(-t * 9f) * Mathf.Sin(2f * Mathf.PI * 76f * t) * 0.7f;
            float rattle = Mathf.Exp(-t * 5f) * lp * 0.8f;
            float crash = Mathf.Exp(-t * 14f) * Noise() * 0.5f;
            d[i] = thump + rattle + crash;
        }
        Normalize(d, 0.95f);
        return ToClip("Jolt_Proc", d, sr);
    }

    // Chirrido metálico de rueda en curva (loop de 2 s).
    AudioClip BuildSqueal(int sr)
    {
        int loop = sr * 2, fade = sr / 5;
        var d = new float[loop + fade];
        double ph = 0;
        for (int i = 0; i < d.Length; i++)
        {
            double t = (double)i / sr;
            double f = 1250 + 120 * System.Math.Sin(2 * System.Math.PI * 3 * t)
                            + 80 * System.Math.Sin(2 * System.Math.PI * 1 * t);
            ph += 2 * System.Math.PI * f / sr;
            float am = 1f + 0.2f * (float)System.Math.Sin(2 * System.Math.PI * 7 * t);
            float s = (float)System.Math.Sin(ph) * 0.6f
                    + (float)System.Math.Sin(2 * ph) * 0.25f
                    + (float)System.Math.Sin(3 * ph + 0.7) * 0.12f;
            d[i] = (s + Noise() * 0.08f) * am;
        }
        var o = MakeLoop(d, loop);
        Normalize(o, 0.6f);
        return ToClip("Squeal_Proc", o, sr);
    }
}
