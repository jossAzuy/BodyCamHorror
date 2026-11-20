using System;
using UnityEngine;
using UnityEngine.UI;

namespace Bodycam
{
    /// <summary>
    /// Bodycam HUD built entirely at runtime (no prefab, no TextMeshPro needed):
    ///  - top-left: blinking REC dot + elapsed recording time
    ///  - top-right: date / time stamp
    ///  - bottom-left: unit / officer id
    ///  - bottom-right: battery that slowly drains
    /// Add it to any GameObject in the scene.
    /// </summary>
    public class BodycamOverlay : MonoBehaviour
    {
        [Header("Text")]
        [SerializeField] private string unitId = "UNIT 12";
        [SerializeField] private string officerId = "ID 4471";
        [SerializeField] private string dateFormat = "yyyy-MM-dd  HH:mm:ss";
        [Tooltip("Use a fixed fake start date instead of the real clock.")]
        [SerializeField] private bool useFakeDate = false;
        [SerializeField] private string fakeStartDate = "2026-03-14 22:41:07";

        [Header("Style")]
        [SerializeField] private Color textColor = new Color(0.92f, 0.92f, 0.92f, 0.9f);
        [SerializeField] private Color recColor = new Color(0.95f, 0.1f, 0.1f, 1f);
        [SerializeField] private int fontSize = 22;
        [SerializeField] private float margin = 36f;
        [SerializeField] private float recBlinkSeconds = 0.8f;

        [Header("Battery")]
        [Range(0f, 1f)] [SerializeField] private float startBattery = 0.92f;
        [Tooltip("Percent lost per real minute.")]
        [SerializeField] private float drainPerMinute = 0.4f;

        private Text _rec, _elapsed, _stamp, _id, _battery;
        private Image _recDot;
        private DateTime _startTime;
        private float _t0;

        private void Awake()
        {
            _startTime = useFakeDate && DateTime.TryParse(fakeStartDate, out var d) ? d : DateTime.Now;
            _t0 = Time.time;
            Build();
        }

        private void Build()
        {
            var canvasGo = new GameObject("BodycamHUD");
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            Font font = LoadFont();

            // REC dot
            var dotGo = new GameObject("RecDot", typeof(RectTransform), typeof(Image));
            dotGo.transform.SetParent(canvasGo.transform, false);
            _recDot = dotGo.GetComponent<Image>();
            _recDot.color = recColor;
            var dr = (RectTransform)dotGo.transform;
            Anchor(dr, new Vector2(0, 1), new Vector2(margin, -margin - 4), new Vector2(18, 18));

            _rec = MakeText(canvasGo.transform, font, "REC", TextAnchor.UpperLeft,
                new Vector2(0, 1), new Vector2(margin + 28, -margin), new Vector2(120, 40));
            _elapsed = MakeText(canvasGo.transform, font, "00:00:00", TextAnchor.UpperLeft,
                new Vector2(0, 1), new Vector2(margin + 100, -margin), new Vector2(220, 40));
            _stamp = MakeText(canvasGo.transform, font, "", TextAnchor.UpperRight,
                new Vector2(1, 1), new Vector2(-margin, -margin), new Vector2(520, 40));
            _id = MakeText(canvasGo.transform, font, unitId + "   " + officerId, TextAnchor.LowerLeft,
                new Vector2(0, 0), new Vector2(margin, margin), new Vector2(520, 40));
            _battery = MakeText(canvasGo.transform, font, "", TextAnchor.LowerRight,
                new Vector2(1, 0), new Vector2(-margin, margin), new Vector2(300, 40));
        }

        private static Font LoadFont()
        {
            try { return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); }
            catch { return Resources.GetBuiltinResource<Font>("Arial.ttf"); }
        }

        private Text MakeText(Transform parent, Font font, string content, TextAnchor align,
            Vector2 anchor, Vector2 pos, Vector2 size)
        {
            var go = new GameObject("Text", typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<Text>();
            t.font = font;
            t.fontSize = fontSize;
            t.fontStyle = FontStyle.Bold;
            t.color = textColor;
            t.alignment = align;
            t.text = content;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.raycastTarget = false;
            var shadow = go.AddComponent<Shadow>();
            shadow.effectColor = new Color(0, 0, 0, 0.8f);
            shadow.effectDistance = new Vector2(1.5f, -1.5f);
            Anchor((RectTransform)go.transform, anchor, pos, size);
            return t;
        }

        private static void Anchor(RectTransform rt, Vector2 anchor, Vector2 pos, Vector2 size)
        {
            rt.anchorMin = rt.anchorMax = anchor;
            rt.pivot = anchor;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
        }

        private void Update()
        {
            float elapsed = Time.time - _t0;
            var ts = TimeSpan.FromSeconds(elapsed);

            _elapsed.text = $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
            _stamp.text = _startTime.AddSeconds(elapsed).ToString(dateFormat);

            bool on = Mathf.Repeat(elapsed, recBlinkSeconds * 2f) < recBlinkSeconds;
            _recDot.enabled = on;

            float bat = Mathf.Max(0f, startBattery - drainPerMinute * 0.01f * (elapsed / 60f));
            _battery.text = $"BAT {Mathf.RoundToInt(bat * 100f)}%";
            _battery.color = bat < 0.15f ? recColor : textColor;
        }
    }
}
