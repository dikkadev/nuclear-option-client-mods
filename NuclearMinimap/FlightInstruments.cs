using System;
using System.Collections.Generic;
using NuclearOption.UIStyleSystem;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NuclearMinimap
{
    internal struct InstrumentOptions
    {
        internal bool HeadingTape, NorthIndicator, Attitude, BankReadout;
        internal InstrumentCorner Corner;
        internal float Size, Opacity;
        internal MapOrientation Orientation;
        internal bool Any => HeadingTape || NorthIndicator && Orientation == MapOrientation.HeadingUp || Attitude;
    }

    // Owned flight overlay above the cockpit minimap: heading tape, north pointer and attitude ball.
    internal sealed class FlightInstruments : IDisposable
    {
        private const float TapeHalfSpan = 35f, BallRange = 40f;
        private static readonly int[] LadderAngles = { -30, -20, -10, 10, 20, 30 };
        private static readonly int[] BankMarks = { -60, -45, -30, -20, -10, 10, 20, 30, 45, 60 };
        private static readonly Color Sky = new Color(.22f, .47f, .78f, .92f), Ground = new Color(.45f, .3f, .15f, .92f);
        private static readonly Color Symbol = new Color(1f, .78f, .1f, 1f), Backing = new Color(0, 0, 0, .55f);
        private readonly RectTransform root;
        private readonly InstrumentMesh tape, tapeBox, north, ball, ballFixed;
        private readonly TextMeshProUGUI[] tapeLabels = new TextMeshProUGUI[4];
        private readonly TextMeshProUGUI headingText, northText, bankText;
        private readonly List<(int degrees, float offset)> ticks = new List<(int, float)>();
        private readonly List<(float x, float y)> circle = new List<(float, float)>(), clipped = new List<(float, float)>();
        private bool disposed;

        internal FlightInstruments(RectTransform parent, TMP_FontAsset font)
        {
            var go = new GameObject("NuclearMinimap.FlightInstruments", typeof(RectTransform), typeof(LayoutElement));
            go.layer = parent.gameObject.layer;
            go.GetComponent<LayoutElement>().ignoreLayout = true;
            root = (RectTransform)go.transform;
            root.SetParent(parent, false);
            Stretch(root);
            try
            {
                tape = Mesh("HeadingTape");
                for (int i = 0; i < tapeLabels.Length; i++) tapeLabels[i] = Text("TapeLabel" + i, font);
                tapeBox = Mesh("HeadingBox");
                headingText = Text("Heading", font);
                north = Mesh("North");
                northText = Text("NorthLabel", font);
                ball = Mesh("AttitudeBall");
                ballFixed = Mesh("AttitudeScale");
                bankText = Text("Bank", font);
            }
            catch { Dispose(); throw; }
        }

        internal static TMP_FontAsset ResolveFont(RectTransform parent)
        {
            var hud = SceneSingleton<FlightHud>.i;
            var native = hud != null ? hud.GetComponentInChildren<TextMeshProUGUI>(true) : null;
            if (native != null && native.font != null) return native.font;
            foreach (var label in parent.GetComponentsInChildren<TextMeshProUGUI>(true))
                if (label.font != null) return label.font;
            if (TMP_Settings.defaultFontAsset != null) return TMP_Settings.defaultFontAsset;
            throw new InvalidOperationException("No native HUD font is available yet.");
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero; rect.pivot = new Vector2(.5f, .5f);
        }

        private InstrumentMesh Mesh(string name)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(InstrumentMesh));
            go.layer = root.gameObject.layer;
            var rect = (RectTransform)go.transform; rect.SetParent(root, false); Stretch(rect);
            var mesh = go.GetComponent<InstrumentMesh>(); mesh.raycastTarget = false;
            return mesh;
        }

        private TextMeshProUGUI Text(string name, TMP_FontAsset font)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            go.layer = root.gameObject.layer;
            var rect = (RectTransform)go.transform; rect.SetParent(root, false);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f);
            var text = go.GetComponent<TextMeshProUGUI>();
            text.font = font; text.fontStyle = FontStyles.Bold;
            text.enableAutoSizing = false; text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Overflow; text.alignment = TextAlignmentOptions.Center;
            text.raycastTarget = false;
            return text;
        }

        private static Color32 Fade(Color color, float opacity) { color.a *= opacity; return color; }

        private static Color LineColor()
        {
            var theme = ThemeManager.Active != null ? ThemeManager.Active.ColorTheme : null;
            var color = theme != null ? theme.AllClear : new Color(.3f, 1f, .3f, 1f);
            color.a = 1;
            return color;
        }

        internal void Update(Transform attitude, InstrumentOptions options)
        {
            if (disposed || !root) return;
            var rect = root.rect;
            if (rect.width <= 0 || rect.height <= 0) return;
            float opacity = Mathf.Clamp(options.Opacity, .1f, 1f);
            Color line = LineColor();
            var forward = attitude.forward;
            float heading = FlightInstrumentMath.Heading(forward.x, forward.z);
            float pitch = FlightInstrumentMath.Pitch(forward.y);
            float bank = FlightInstrumentMath.Bank(attitude.right.y, attitude.up.y);
            float s = Mathf.Min(rect.width, rect.height), margin = s * .025f, stroke = Mathf.Max(1f, s * .006f);
            float tapeBottom = UpdateTape(options.HeadingTape, rect, s, margin, stroke, heading, line, opacity);
            UpdateNorth(options.NorthIndicator && options.Orientation == MapOrientation.HeadingUp, rect, s, margin, stroke, heading, line, opacity);
            UpdateBall(options, rect, s, margin, stroke, pitch, bank, options.HeadingTape ? tapeBottom : rect.yMax, line, opacity);
        }

        private float UpdateTape(bool show, Rect rect, float s, float margin, float stroke, float heading, Color line, float opacity)
        {
            tape.Begin(); tapeBox.Begin();
            float width = rect.width * .56f, height = s * .09f, center = rect.center.x;
            float top = rect.yMax - margin, bottom = top - height;
            float boxWidth = height * 1.9f, boxHeight = height * 1.1f;
            int label = 0;
            if (show)
            {
                float unitsPerDegree = width * .5f / TapeHalfSpan;
                var lineColor = Fade(line, opacity);
                tape.Box(new Rect(center - width * .5f, bottom, width, height), Fade(Backing, opacity));
                FlightInstrumentMath.TapeTicks(heading, TapeHalfSpan, 5, ticks);
                foreach (var tick in ticks)
                {
                    float x = center + tick.offset * unitsPerDegree;
                    float length = height * (tick.degrees % 30 == 0 ? .42f : tick.degrees % 10 == 0 ? .3f : .17f);
                    tape.Line(new Vector2(x, bottom), new Vector2(x, bottom + length), stroke, lineColor);
                    if (tick.degrees % 30 != 0 || label >= tapeLabels.Length) continue;
                    float fontSize = height * .46f;
                    // Hide labels under the readout box.
                    if (Mathf.Abs(x - center) < boxWidth * .5f + fontSize * .7f) continue;
                    Place(tapeLabels[label++], FlightInstrumentMath.TapeLabel(tick.degrees), new Vector2(x, bottom + height * .7f), fontSize, line, opacity);
                }
                var box = new Rect(center - boxWidth * .5f, bottom + (height - boxHeight) * .5f, boxWidth, boxHeight);
                tapeBox.Box(box, Fade(new Color(0, 0, 0, .85f), opacity));
                tapeBox.Outline(box, stroke, lineColor);
                tapeBox.Triangle(new Vector2(center, box.yMin - stroke), new Vector2(center - height * .2f, box.yMin - height * .32f),
                    new Vector2(center + height * .2f, box.yMin - height * .32f), lineColor);
                Place(headingText, FlightInstrumentMath.HeadingText(heading), box.center, height * .66f, line, opacity);
            }
            else headingText.gameObject.SetActive(false);
            for (int i = label; i < tapeLabels.Length; i++) tapeLabels[i].gameObject.SetActive(false);
            tape.End(); tapeBox.End();
            return bottom - height * .32f;
        }

        private void UpdateNorth(bool show, Rect rect, float s, float margin, float stroke, float heading, Color line, float opacity)
        {
            north.Begin();
            if (show)
            {
                float radius = s * .065f;
                var center = new Vector2(rect.xMin + margin + radius, rect.yMax - margin - radius);
                // The heading-up map is rotated counter-clockwise by heading, so north is too.
                float angle = MapZoom.Angle(heading, MapOrientation.HeadingUp) * Mathf.Deg2Rad;
                var up = new Vector2(-Mathf.Sin(angle), Mathf.Cos(angle)); var side = new Vector2(up.y, -up.x);
                north.Disc(center, radius, 32, Fade(Backing, opacity));
                north.Ring(center, radius - stroke, radius, 32, Fade(line, opacity));
                north.Triangle(center + up * radius * .86f, center + up * radius * .05f + side * radius * .36f,
                    center + up * radius * .05f - side * radius * .36f, Fade(line, opacity));
                Place(northText, "N", center - up * radius * .42f, radius * .72f, line, opacity);
            }
            else northText.gameObject.SetActive(false);
            north.End();
        }

        private void UpdateBall(InstrumentOptions options, Rect rect, float s, float margin, float stroke, float pitch, float bank,
            float topLimit, Color line, float opacity)
        {
            ball.Begin(); ballFixed.Begin();
            bool show = options.Attitude;
            if (show)
            {
                float outer = Mathf.Clamp(options.Size, .15f, .5f) * s * .5f, radius = outer / 1.2f;
                var position = FlightInstrumentMath.CornerCenter(options.Corner, rect.xMin, rect.yMin, rect.xMax, rect.yMax, outer, margin);
                var center = new Vector2(position.x, position.y);
                // A top-corner ball moves below the heading tape when the two would overlap.
                if (FlightInstrumentMath.IsTop(options.Corner) && options.HeadingTape &&
                    Mathf.Abs(center.x - rect.center.x) - outer < rect.width * .28f)
                    center.y = Mathf.Min(center.y, topLimit - margin - outer);
                float angle = bank * Mathf.Deg2Rad, unitsPerDegree = radius / BallRange;
                var up = new Vector2(-Mathf.Sin(angle), Mathf.Cos(angle)); var along = new Vector2(up.y, -up.x);
                float horizon = -pitch * unitsPerDegree;
                circle.Clear();
                for (int i = 0; i < 48; i++)
                {
                    float a = i * Mathf.PI * 2 / 48;
                    circle.Add((Mathf.Cos(a) * radius, Mathf.Sin(a) * radius));
                }
                FlightInstrumentMath.ClipHalfPlane(circle, up.x, up.y, horizon, clipped);
                ball.Polygon(clipped, center, Fade(Sky, opacity));
                FlightInstrumentMath.ClipHalfPlane(circle, -up.x, -up.y, -horizon, clipped);
                ball.Polygon(clipped, center, Fade(Ground, opacity));
                var white = Fade(Color.white, opacity);
                Chord(center, up, along, horizon, radius, radius, stroke * 1.5f, white);
                foreach (int mark in LadderAngles)
                {
                    float offset = FlightInstrumentMath.LadderOffset(pitch, mark, unitsPerDegree);
                    Chord(center, up, along, offset, radius * .9f, radius * (Math.Abs(mark) == 20 ? .3f : .18f), stroke, white);
                }
                // Sky pointer turns with the horizon against the fixed bank scale.
                ball.Triangle(center + up * radius * .97f, center + up * radius * .8f + along * radius * .09f,
                    center + up * radius * .8f - along * radius * .09f, Fade(Symbol, opacity));
                var lineColor = Fade(line, opacity);
                ballFixed.Ring(center, radius, radius + stroke * 1.5f, 48, Fade(Backing, opacity));
                ballFixed.Ring(center, radius + stroke * .5f, radius + stroke * 1.2f, 48, lineColor);
                foreach (int mark in BankMarks)
                {
                    float a = mark * Mathf.Deg2Rad;
                    var direction = new Vector2(-Mathf.Sin(a), Mathf.Cos(a));
                    float length = Math.Abs(mark) == 30 || Math.Abs(mark) == 60 ? .16f : .09f;
                    ballFixed.Line(center + direction * (radius + stroke), center + direction * radius * (1.04f + length), stroke, lineColor);
                }
                ballFixed.Triangle(center + Vector2.up * (radius + stroke), center + new Vector2(-radius * .08f, radius * 1.17f),
                    center + new Vector2(radius * .08f, radius * 1.17f), lineColor);
                // Fixed aircraft reference.
                var symbol = Fade(Symbol, opacity);
                float wing = radius * .055f;
                ballFixed.Box(new Rect(center.x - radius * .55f, center.y - wing * .5f, radius * .36f, wing), symbol);
                ballFixed.Box(new Rect(center.x + radius * .19f, center.y - wing * .5f, radius * .36f, wing), symbol);
                ballFixed.Box(new Rect(center.x - wing, center.y - wing, wing * 2, wing * 2), symbol);
                if (options.BankReadout)
                {
                    float size = radius * .36f;
                    float y = FlightInstrumentMath.IsTop(options.Corner) ? center.y - outer - size * .7f : center.y + outer + size * .7f;
                    Place(bankText, FlightInstrumentMath.BankText(bank), new Vector2(center.x, y), size, line, opacity);
                }
                else bankText.gameObject.SetActive(false);
            }
            else bankText.gameObject.SetActive(false);
            ball.End(); ballFixed.End();
        }

        // A line parallel to the horizon at the given offset, limited by the ball's chord.
        private void Chord(Vector2 center, Vector2 up, Vector2 along, float offset, float limit, float halfLength, float width, Color32 color)
        {
            if (Mathf.Abs(offset) >= limit) return;
            halfLength = Mathf.Min(halfLength, Mathf.Sqrt(limit * limit - offset * offset));
            var middle = center + up * offset;
            ball.Line(middle - along * halfLength, middle + along * halfLength, width, color);
        }

        private static void Place(TextMeshProUGUI text, string content, Vector2 position, float size, Color color, float opacity)
        {
            if (text.text != content) text.text = content;
            color.a = opacity;
            text.color = color; text.fontSize = size;
            var rect = text.rectTransform;
            rect.anchoredPosition = position; rect.sizeDelta = new Vector2(size * 4, size * 1.4f);
            if (!text.gameObject.activeSelf) text.gameObject.SetActive(true);
        }

        internal void SetVisible(bool visible)
        {
            if (!disposed && root && root.gameObject.activeSelf != visible) root.gameObject.SetActive(visible);
        }

        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (root) { root.gameObject.SetActive(false); UnityEngine.Object.Destroy(root.gameObject); }
        }
    }
}
