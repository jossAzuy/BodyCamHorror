using System;
using UnityEngine;
using UnityEngine.UI;

namespace Bodycam
{
    /// <summary>Every element of the bodycam HUD that can be shown or hidden.</summary>
    [Flags]
    public enum OverlayElements
    {
        RecDot      = 1 << 0,   // blinking red dot
        RecLabel    = 1 << 1,   // "REC" text
        ElapsedTime = 1 << 2,   // 00:00:00 recording time
        Timestamp   = 1 << 3,   // date / time stamp (top-right)
        UnitId      = 1 << 4,   // "UNIT 12"
        OfficerId   = 1 << 5,   // "ID 4471"
        Battery     = 1 << 6    // "BAT 92%"
    }

    /// <summary>
    /// Bodycam HUD built entirely at runtime (no prefab, no TextMeshPro needed):
    ///  - top-left: blinking REC dot + REC label + elapsed recording time
    ///  - top-right: date / time stamp
    ///  - bottom-left: unit id and officer id
    ///  - bottom-right: battery that slowly drains
    /// Every element can be shown or hidden from the inspector ("Visible Elements"),
    /// at runtime, or from other scripts (SetVisible / SetHudActive).
    /// The top-left row re-flows automatically when elements are hidden.
    /// Add it to any GameObject in the scene.
    /// </summary>
    public class BodycamOverlay : MonoBehaviour
    {
        private const OverlayElements AllElements =
            OverlayElements.RecDot | OverlayElements.RecLabel | OverlayElements.ElapsedTime |
            OverlayElements.Timestamp | OverlayElements.UnitId | OverlayElements.OfficerId |
            OverlayElements.Battery;

        [Header("Visible elements")]
        [Tooltip("Tick the elements you want on screen. Changes apply live in Play mode.")]
        [SerializeField] private OverlayElements visible = AllElements;

        [Header("Text")]
        [SerializeField] private string recLabel = "REC";
        [SerializeField] private string unitId = "UNIT 12";
        [SerializeField] private string officerId = "ID 4471";
        [Tooltip("Text placed between the unit id and the officer id when both are shown.")]
        [SerializeField] private string idSeparator = "   ";
        [SerializeField] private string batteryPrefix = "BAT";
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

        private const float DotSize = 18f;
        private const float DotGap = 10f;
        private const float RowGap = 26f;

        private Canvas _canvas;
        private Text _rec, _elapsed, _stamp, _id, _battery;
        private Image _recDot;
        private DateTime _startTime;
        private float _t0;

        // Cached state so layout is only recomputed when something changed.
        private OverlayElements _lastVisible;
        private string _lastRecLabel, _lastIdString;
        private int _lastFontSize;
        private float _lastMargin;
        private bool _layoutBuilt;

        public OverlayElements Visible
        {
            get => visible;
            set => visible = value;
        }

        public bool IsVisible(OverlayElements element) => (visible & element) != 0;

        public void SetVisible(OverlayElements element, bool on)
        {
            if (on) visible |= element;
            else visible &= ~element;
        }

        /// <summary>Shows or hides the whole HUD without losing the recording timer.</summary>
        public void SetHudActive(bool on)
        {
            if (_canvas != null) _canvas.gameObject.SetActive(on);
        }

        private bool Has(OverlayElements e) => (visible & e) != 0;

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
            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 100;
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
            _recDot.raycastTarget = false;
            Anchor((RectTransform)dotGo.transform, new Vector2(0, 1), Vector2.zero, new Vector2(DotSize, DotSize));

            _rec = MakeText(canvasGo.transform, font, recLabel, TextAnchor.UpperLeft,
                new Vector2(0, 1), new Vector2(0, 0), new Vector2(160, 40));
            _elapsed = MakeText(canvasGo.transform, font, "00:00:00", TextAnchor.UpperLeft,
                new Vector2(0, 1), new Vector2(0, 0), new Vector2(220, 40));
            _stamp = MakeText(canvasGo.transform, font, "", TextAnchor.UpperRight,
                new Vector2(1, 1), new Vector2(0, 0), new Vector2(520, 40));
            _id = MakeText(canvasGo.transform, font, "", TextAnchor.LowerLeft,
                new Vector2(0, 0), new Vector2(0, 0), new Vector2(520, 40));
            _battery = MakeText(canvasGo.transform, font, "", TextAnchor.LowerRight,
                new Vector2(1, 0), new Vector2(0, 0), new Vector2(300, 40));

            RefreshLayout(force: true);
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

        private string BuildIdString()
        {
            bool u = Has(OverlayElements.UnitId) && !string.IsNullOrEmpty(unitId);
            bool o = Has(OverlayElements.OfficerId) && !string.IsNullOrEmpty(officerId);
            if (u && o) return unitId + idSeparator + officerId;
            if (u) return unitId;
            if (o) return officerId;
            return "";
        }

        /// <summary>
        /// Applies visibility and positions. Only runs when something relevant changed.
        /// </summary>
        private void RefreshLayout(bool force = false)
        {
            string idString = BuildIdString();

            bool changed = force
                || visible != _lastVisible
                || recLabel != _lastRecLabel
                || idString != _lastIdString
                || fontSize != _lastFontSize
                || !Mathf.Approximately(margin, _lastMargin);

            if (!changed) return;

            _lastVisible = visible;
            _lastRecLabel = recLabel;
            _lastIdString = idString;
            _lastFontSize = fontSize;
            _lastMargin = margin;

            foreach (var t in new[] { _rec, _elapsed, _stamp, _id, _battery })
                t.fontSize = fontSize;

            _rec.text = recLabel;
            _id.text = idString;

            // Visibility
            _recDot.gameObject.SetActive(Has(OverlayElements.RecDot));
            _rec.gameObject.SetActive(Has(OverlayElements.RecLabel) && !string.IsNullOrEmpty(recLabel));
            _elapsed.gameObject.SetActive(Has(OverlayElements.ElapsedTime));
            _stamp.gameObject.SetActive(Has(OverlayElements.Timestamp));
            _id.gameObject.SetActive(idString.Length > 0);
            _battery.gameObject.SetActive(Has(OverlayElements.Battery));

            // Top-left row: dot -> label -> time, packed left to right so there are no holes.
            float x = margin;
            if (_recDot.gameObject.activeSelf)
            {
                ((RectTransform)_recDot.transform).anchoredPosition = new Vector2(x, -margin - 4f);
                x += DotSize + DotGap;
            }
            if (_rec.gameObject.activeSelf)
            {
                ((RectTransform)_rec.transform).anchoredPosition = new Vector2(x, -margin);
                x += _rec.preferredWidth + RowGap;
            }
            if (_elapsed.gameObject.activeSelf)
            {
                ((RectTransform)_elapsed.transform).anchoredPosition = new Vector2(x, -margin);
            }

            // Corners
            ((RectTransform)_stamp.transform).anchoredPosition = new Vector2(-margin, -margin);
            ((RectTransform)_id.transform).anchoredPosition = new Vector2(margin, margin);
            ((RectTransform)_battery.transform).anchoredPosition = new Vector2(-margin, margin);
        }

        private void Update()
        {
            RefreshLayout();

            // The recording timer keeps running even if the elements are hidden.
            float elapsed = Time.time - _t0;

            if (Has(OverlayElements.ElapsedTime))
            {
                var ts = TimeSpan.FromSeconds(elapsed);
                _elapsed.text = $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
            }

            if (Has(OverlayElements.Timestamp))
                _stamp.text = _startTime.AddSeconds(elapsed).ToString(dateFormat);

            if (Has(OverlayElements.RecDot))
                _recDot.enabled = Mathf.Repeat(elapsed, recBlinkSeconds * 2f) < recBlinkSeconds;

            if (Has(OverlayElements.Battery))
            {
                float bat = Mathf.Max(0f, startBattery - drainPerMinute * 0.01f * (elapsed / 60f));
                _battery.text = $"{batteryPrefix} {Mathf.RoundToInt(bat * 100f)}%";
                _battery.color = bat < 0.15f ? recColor : textColor;
            }
        }
    }
}