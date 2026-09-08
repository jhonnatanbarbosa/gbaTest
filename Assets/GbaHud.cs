using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Altitude, airspeed and throttle, drawn at native GBA resolution and labelled, so
/// that somebody who has never seen the game can tell which bar is which.
///
/// Attitude is two instruments rather than three gauges. An artificial horizon in the
/// middle of the screen carries pitch and roll at once -- it tilts with the wings and
/// slides with the nose -- and a heading tape along the top carries yaw, because yaw is
/// not somewhere a plane sits, it is the heading it ends up on. The tape doubles as the
/// compass: N, E, S and W slide past a fixed index.
///
/// Both are built from axis-aligned one and three pixel quads that are snapped to whole
/// pixels every frame. Nothing here is ever rotated. A rotated quad comes back from the
/// GPU with antialiased edges, the render texture is point sampled so nothing smooths
/// them out, and the palette shader then rounds each grey fringe to whichever of four
/// colours it lands nearest -- which is a horizon that crawls and sparkles as it tips.
/// A row of dots along the same line has nothing in it to round.
///
/// Builds its own canvas in Screen Space - Camera mode on the camera that renders
/// into the 160x144 render texture, so the HUD is captured by that texture and goes
/// through the palette shader with everything else. One canvas unit is one GBA pixel,
/// so every size below is literally in screen pixels.
///
/// Bars are plain Images with no sprite, which uGUI renders as a solid quad. Text is
/// <see cref="PixelFont"/>, which is hand-placed pixels for the same reason: at this
/// size a real font is antialiased into grey fringes that the palette shader then
/// rounds one way on one frame and the other way on the next.
///
/// The colours are greys, and deliberately so. The palette shader sorts every pixel
/// into one of four buckets by brightness and dithers anything that lands near a
/// bucket edge. The four defaults below sit dead in the middle of the four buckets,
/// which is what keeps the gauges solid instead of crawling. If you change them, keep
/// their brightness at 0, 1/3, 2/3 and 1.
/// </summary>
public class GbaHud : MonoBehaviour
{
    [Header("Wiring")]
    [Tooltip("The plane to read. Found automatically if left empty.")]
    [SerializeField] private AirshipController plane;
    [Tooltip("The camera that renders into the GBA render texture. Found automatically if left empty.")]
    [SerializeField] private Camera gbaCamera;

    [Header("Altitude gauge")]
    [Tooltip("Height in pixels of the whole vertical track. Half of it is above zero, half below.")]
    [SerializeField] private int altitudeTrackHeight = 96;
    [SerializeField] private int altitudeTrackWidth = 5;
    [Tooltip("Altitude at which the gauge is full. Above this it just pins to the top.")]
    [SerializeField] private float maxDisplayAltitude = 150f;

    [Header("Speed and throttle bars")]
    [SerializeField] private int barWidth = 56;
    [SerializeField] private int barHeight = 5;
    [Tooltip("Airspeed at which the speed bar is full. Set this near the plane's top speed.")]
    [SerializeField] private float maxDisplaySpeed = 30f;

    [Header("Layout")]
    [Tooltip("Gap in pixels between the gauges and the edge of the screen.")]
    [SerializeField] private int margin = 6;
    [Tooltip("ALT, SPD and THR next to their bars. Eleven pixels each, and the difference between a gauge and a mystery.")]
    [SerializeField] private bool showLabels = true;
    [Tooltip("Altitude, airspeed and heading as numbers as well as gauges.")]
    [SerializeField] private bool showValues = true;
    [Tooltip("A dark copy of every glyph one pixel down and right. Text sits over open sky half the time, and with four colours to play with this is what keeps it readable.")]
    [SerializeField] private bool textShadow = true;

    [Header("Attitude indicator")]
    [Tooltip("A horizon that tilts with the wings and rides up and down with the nose, over a fixed aircraft mark. Roll and pitch in one place, which is the one instrument every cockpit has agreed on for a century.")]
    [SerializeField] private bool showAttitude = true;
    [Tooltip("How wide the horizon is drawn, in pixels.")]
    [SerializeField] private int horizonSpan = 46;
    [Tooltip("How many dots that span is made of. Dots rather than a drawn line, because a rotated quad comes back from the GPU with grey antialiased edges, and grey is precisely what a four colour screen cannot hold still.")]
    [SerializeField] private int horizonDots = 11;
    [Tooltip("Dots closer to the middle than this are dropped, so the aircraft mark is never buried under its own horizon.")]
    [SerializeField] private int horizonGap = 11;
    [Tooltip("Pixels the horizon slides per degree of pitch.")]
    [SerializeField] private float pitchPixels = 0.5f;
    [Tooltip("How far the horizon may travel from the middle before it stops being drawn at all. Past this the nose is steep enough that the number on the gauge is the honest answer.")]
    [SerializeField] private int horizonRange = 26;

    [Header("Compass")]
    [Tooltip("A heading tape across the top, with N, E, S and W sliding past a fixed index. This is the yaw gauge as well: heading is where the nose has been yawed to.")]
    [SerializeField] private bool showCompass = true;
    [Tooltip("Width of the tape in pixels.")]
    [SerializeField] private int compassWidth = 88;
    [Tooltip("Degrees of heading the whole tape spans. Smaller magnifies a small turn.")]
    [SerializeField] private float compassRange = 120f;
    [Tooltip("Degrees between ticks. The four cardinals get a letter instead of a tick wherever they land.")]
    [SerializeField] private float compassTickStep = 15f;

    [Header("Stall warning")]
    [Tooltip("Blink STALL across the middle of the screen when the wing gives up. The camera is already shaking by then, but a first-timer needs telling why.")]
    [SerializeField] private bool showStallWarning = true;
    [Tooltip("Blinks per second.")]
    [SerializeField] private float stallBlinkRate = 3f;

    [Header("Palette")]
    [Tooltip("Darkest of the four. Gauge backgrounds and the shadow behind text.")]
    [SerializeField] private Color trackColor = new Color(0f, 0f, 0f, 1f);
    [Tooltip("Lightest of the four. The moving part of every gauge, and the text.")]
    [SerializeField] private Color fillColor = new Color(1f, 1f, 1f, 1f);
    [Tooltip("Second lightest. Ticks, the sea-level line, and the bar when the plane drops below it.")]
    [SerializeField] private Color markColor = new Color(0.667f, 0.667f, 0.667f, 1f);

    private RectTransform altitudeFill;
    private Image altitudeFillImage;
    private RectTransform speedFill;
    private RectTransform throttleFill;

    private PixelLabel altitudeValue;
    private PixelLabel speedValue;
    private PixelLabel stallWarning;

    private PixelMark[] horizonMarks;
    private PixelMark[] compassTicks;
    private PixelLabel[] compassCardinals;
    private PixelLabel headingValue;

    private Transform planeTransform;

    // Held rather than recomputed when the nose goes vertical, where a heading is not a
    // thing that exists and the tape would otherwise spin on whatever the last rounding
    // error happened to be.
    private float heading;

    private static readonly string[] CardinalNames = { "N", "E", "S", "W" };
    private static readonly float[] CardinalAngles = { 0f, 90f, 180f, 270f };

    private void Awake()
    {
        if (plane == null)
            plane = FindFirstObjectByType<AirshipController>();

        if (gbaCamera == null)
            gbaCamera = Camera.main;

        if (plane != null)
            planeTransform = plane.transform;

        Build();
    }

    private void Update()
    {
        if (plane == null)
            return;

        UpdateAltitude(plane.Altitude);
        UpdateSpeed(plane.Airspeed);
        UpdateThrottle(plane.Throttle);
        UpdateAttitude();
        UpdateCompass();
        UpdateStallWarning();
    }

    private void UpdateAltitude(float altitude)
    {
        float half = altitudeTrackHeight * 0.5f;
        float normalized = Mathf.Clamp(altitude / Mathf.Max(maxDisplayAltitude, 0.01f), -1f, 1f);

        // The bar grows out of the middle of the track, so the y = 0 line stays put
        // and the fill reads as distance from it. Flipping the pivot is what lets the
        // same rect grow upward when climbing and downward when below sea level.
        altitudeFill.pivot = new Vector2(0.5f, normalized >= 0f ? 0f : 1f);
        altitudeFill.sizeDelta = new Vector2(0f, Mathf.Round(Mathf.Abs(normalized) * half));

        // Below zero reads as a warning, so it swaps to the marker colour.
        altitudeFillImage.color = normalized >= 0f ? fillColor : markColor;

        altitudeValue?.Set(Mathf.Clamp(Mathf.RoundToInt(altitude), -99, 999).ToString());
    }

    private void UpdateSpeed(float airspeed)
    {
        float normalized = Mathf.Clamp01(airspeed / Mathf.Max(maxDisplaySpeed, 0.01f));

        // Round to whole pixels, or the end of the bar shimmers between two columns
        // as the speed drifts, which at this resolution is very obvious.
        speedFill.sizeDelta = new Vector2(Mathf.Round(normalized * barWidth), 0f);

        speedValue?.Set(Mathf.Clamp(Mathf.RoundToInt(airspeed), 0, 999).ToString());
    }

    private void UpdateThrottle(float throttle)
    {
        throttleFill.sizeDelta = new Vector2(Mathf.Round(Mathf.Clamp01(throttle) * barWidth), 0f);
    }

    /// <summary>
    /// Roll and pitch, read off the plane's own axes rather than out of Euler angles.
    ///
    /// Euler angles would need a convention, an order and a gimbal lock exception; two
    /// dot products need none of the three and stay correct upside down. The instrument's
    /// screen is the plane's own right and up, so projecting a world-level direction onto
    /// that pair gives the horizon's screen direction straight out, sign included.
    ///
    /// Read from the transform rather than from the controller's measured bank, because
    /// the transform is interpolated between physics steps and this runs every frame:
    /// the same number off the last FixedUpdate would step where the horizon should glide.
    /// </summary>
    private void UpdateAttitude()
    {
        if (horizonMarks == null)
            return;

        Vector3 forward = planeTransform.forward;

        // The horizon runs along whatever is both level and across the nose. Pointed
        // straight up or straight down there is no such direction, and that is exactly
        // when an artificial horizon has nothing true to say, so it stops rather than
        // inventing an angle out of a vector with no length left in it.
        Vector3 levelRight = Vector3.Cross(Vector3.up, forward);
        if (levelRight.sqrMagnitude < 1e-6f)
        {
            HideHorizon();
            return;
        }

        levelRight.Normalize();

        Vector2 direction = new Vector2(
            Vector3.Dot(levelRight, planeTransform.right),
            Vector3.Dot(levelRight, planeTransform.up));

        if (direction.sqrMagnitude < 1e-6f)
        {
            HideHorizon();
            return;
        }

        direction.Normalize();

        // Nose up puts the horizon below the aircraft mark. That is the convention every
        // attitude indicator uses, and it is the right way round: the mark is the plane,
        // so the ground going down the screen is the nose coming up.
        float pitch = Mathf.Asin(Mathf.Clamp(forward.y, -1f, 1f)) * Mathf.Rad2Deg;
        Vector2 normal = new Vector2(-direction.y, direction.x);
        Vector2 centre = normal * (-pitch * pitchPixels);

        float reach = horizonSpan * 0.5f;

        for (int i = 0; i < horizonMarks.Length; i++)
        {
            float along = horizonMarks.Length == 1
                ? 0f
                : Mathf.Lerp(-reach, reach, i / (horizonMarks.Length - 1f));

            if (Mathf.Abs(along) < horizonGap)
            {
                horizonMarks[i].Visible = false;
                continue;
            }

            Vector2 point = centre + direction * along;

            // Let it run off rather than pinning it to the edge: a horizon clamped to the
            // top of its window reads as level flight, which is a lie worth avoiding.
            bool inside = Mathf.Abs(point.y) <= horizonRange && Mathf.Abs(point.x) <= reach + 2f;
            horizonMarks[i].Visible = inside;

            if (inside)
                horizonMarks[i].Place(point);
        }
    }

    private void HideHorizon()
    {
        for (int i = 0; i < horizonMarks.Length; i++)
            horizonMarks[i].Visible = false;
    }

    /// <summary>
    /// Where the nose points on the ground, which is both the compass and the yaw gauge:
    /// yaw is not a place you can sit at, it is the heading it leaves you on.
    /// </summary>
    private void UpdateCompass()
    {
        if (compassTicks == null)
            return;

        Vector3 forward = planeTransform.forward;
        Vector2 flat = new Vector2(forward.x, forward.z);

        // Straight up or straight down has no heading at all. Holding the last one keeps
        // the tape still through a vertical climb instead of letting it spin.
        if (flat.sqrMagnitude > 1e-6f)
            heading = Mathf.Repeat(Mathf.Atan2(flat.x, flat.y) * Mathf.Rad2Deg, 360f);

        float half = compassRange * 0.5f;
        float pixelsPerDegree = compassWidth / Mathf.Max(compassRange, 1f);
        float step = Mathf.Max(compassTickStep, 1f);

        // Stepped by an integer index rather than by adding the step to a float, so a
        // tape left running for an hour still puts its ticks on exact multiples.
        int first = Mathf.CeilToInt((heading - half) / step);
        int last = Mathf.FloorToInt((heading + half) / step);
        int used = 0;

        for (int i = first; i <= last && used < compassTicks.Length; i++)
        {
            float angle = i * step;

            // The cardinals get a letter instead of a tick, placed below.
            float intoQuadrant = Mathf.Repeat(angle, 90f);
            if (Mathf.Min(intoQuadrant, 90f - intoQuadrant) < 0.01f)
                continue;

            float delta = Mathf.DeltaAngle(heading, angle);
            if (Mathf.Abs(delta) > half)
                continue;

            compassTicks[used].Visible = true;
            compassTicks[used].Place(new Vector2(delta * pixelsPerDegree, 0f));
            used++;
        }

        for (int i = used; i < compassTicks.Length; i++)
            compassTicks[i].Visible = false;

        for (int i = 0; i < compassCardinals.Length; i++)
        {
            float delta = Mathf.DeltaAngle(heading, CardinalAngles[i]);
            bool visible = Mathf.Abs(delta) <= half;
            compassCardinals[i].Visible = visible;

            if (!visible)
                continue;

            // Half a glyph left of the tick it marks, so the letter straddles its own
            // heading rather than starting at it.
            float x = Mathf.Round(delta * pixelsPerDegree) - PixelFont.Width / 2;
            compassCardinals[i].Root.anchoredPosition = new Vector2(x, CardinalRow);
        }

        // 000 at north and 090 at east, padded so the number never changes width and
        // shuffles its own digits sideways as it ticks over.
        headingValue?.Set((Mathf.RoundToInt(heading) % 360).ToString("000"));
    }

    private void UpdateStallWarning()
    {
        if (stallWarning == null)
            return;

        // Blinking rather than steady, because a static word in the middle of the
        // screen stops being read after about four seconds.
        bool on = plane.IsStalling && Mathf.Repeat(Time.time * Mathf.Max(stallBlinkRate, 0.01f), 1f) < 0.5f;
        stallWarning.Visible = on;
    }

    // --- Construction ------------------------------------------------------

    private void Build()
    {
        Canvas canvas = BuildCanvas();

        BuildAltitudeGauge(canvas.transform);

        // Two rows in the bottom left, throttle above speed, both starting at the same
        // column so the labels line up and the bars can be compared at a glance.
        int labelWidth = showLabels ? PixelFont.TextWidth("SPD") + 3 : 0;
        int barLeft = margin + labelWidth;

        throttleFill = BuildBar("Throttle", canvas.transform, "THR",
            new Vector2(barLeft, margin + barHeight + 3), out _);

        speedFill = BuildBar("Speed", canvas.transform, "SPD",
            new Vector2(barLeft, margin), out RectTransform speedTrack);

        // A tick where the wing gives up, so "keep the bar right of this line" is
        // something you can see rather than something you have to be told.
        if (plane != null && maxDisplaySpeed > 0.01f)
        {
            RectTransform stallMark = CreateImage("StallMark", speedTrack, markColor);
            stallMark.anchorMin = Vector2.zero;
            stallMark.anchorMax = new Vector2(0f, 1f);
            stallMark.pivot = Vector2.zero;
            stallMark.anchoredPosition = new Vector2(Mathf.Round(plane.StallSpeed / maxDisplaySpeed * barWidth), 0f);
            stallMark.sizeDelta = new Vector2(1f, 0f);
        }

        if (showValues)
        {
            // Right of the speed bar, where there is room for three digits.
            speedValue = new PixelLabel("SpeedValue", canvas.transform, fillColor, trackColor, textShadow);
            Place(speedValue.Root, Vector2.zero, new Vector2(0f, 0f),
                new Vector2(barLeft + barWidth + 3, margin));
        }

        if (showAttitude)
            BuildAttitudeIndicator(canvas.transform);

        if (showCompass)
            BuildCompass(canvas.transform);

        if (showStallWarning)
        {
            stallWarning = new PixelLabel("StallWarning", canvas.transform, fillColor, trackColor, textShadow);
            Place(stallWarning.Root, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 24f));
            stallWarning.Set("STALL");
            stallWarning.Visible = false;
        }
    }

    private void BuildAltitudeGauge(Transform parent)
    {
        // A vertical track against the right edge, centred on screen.
        RectTransform track = CreateImage("AltitudeTrack", parent, trackColor);
        Place(track, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-margin, 0f));
        track.sizeDelta = new Vector2(altitudeTrackWidth, altitudeTrackHeight);

        // Anchored to the track's middle and stretched to its width, so only the
        // height below has to change at runtime.
        altitudeFill = CreateImage("AltitudeFill", track, fillColor);
        altitudeFill.anchorMin = new Vector2(0f, 0.5f);
        altitudeFill.anchorMax = new Vector2(1f, 0.5f);
        altitudeFill.anchoredPosition = Vector2.zero;
        altitudeFillImage = altitudeFill.GetComponent<Image>();

        // Added after the fill so it draws over the top of it.
        RectTransform zeroLine = CreateImage("ZeroLine", track, markColor);
        zeroLine.anchorMin = new Vector2(0f, 0.5f);
        zeroLine.anchorMax = new Vector2(1f, 0.5f);
        zeroLine.pivot = new Vector2(0.5f, 0.5f);
        zeroLine.anchoredPosition = Vector2.zero;
        zeroLine.sizeDelta = new Vector2(4f, 1f);   // two pixels proud of the track on each side

        // Label above the track and the number below it, both hung off the track's
        // right edge so they stay inside the screen however wide they get.
        if (showLabels)
        {
            PixelLabel label = new PixelLabel("AltitudeLabel", track, fillColor, trackColor, textShadow);
            Place(label.Root, new Vector2(1f, 1f), new Vector2(1f, 0f), new Vector2(0f, 3f));
            label.Set("ALT");
        }

        if (showValues)
        {
            altitudeValue = new PixelLabel("AltitudeValue", track, fillColor, trackColor, textShadow);
            Place(altitudeValue.Root, new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, -3f));
        }
    }

    // Rows inside the compass group, measured in pixels from the tick row.
    private const int CompassTop = 12;    // down from the top of the screen to the ticks
    private const int CardinalRow = -6;   // letters under the ticks
    private const int HeadingRow = -13;   // the number under the letters

    /// <summary>
    /// The artificial horizon, dead centre, where the eye already is.
    ///
    /// Deliberately not boxed. A framed instrument would cost thirty by thirty pixels of
    /// a screen that only has a hundred and sixty across, and would put an opaque panel
    /// exactly where the player is trying to look. A bare row of dots reads as a horizon
    /// just as well and takes almost nothing away from the view.
    /// </summary>
    private void BuildAttitudeIndicator(Transform parent)
    {
        RectTransform group = NewGroup("Attitude", parent, new Vector2(0.5f, 0.5f), Vector2.zero);

        horizonMarks = new PixelMark[Mathf.Max(horizonDots, 2)];
        for (int i = 0; i < horizonMarks.Length; i++)
            horizonMarks[i] = new PixelMark("HorizonDot", group, Vector2.one, fillColor, trackColor, textShadow);

        // The aeroplane: two stub wings and a dot. Built after the horizon so it draws
        // over the top of it, and the one thing on the gauge that never moves -- every
        // angle the horizon shows is only an angle relative to this.
        BuildMark("ReticleLeft", group, new Vector2(-9f, 0f), new Vector2(4f, 1f));
        BuildMark("ReticleRight", group, new Vector2(6f, 0f), new Vector2(4f, 1f));
        BuildMark("ReticleCentre", group, Vector2.zero, Vector2.one);
    }

    /// <summary>
    /// The heading tape, hung off the top of the screen. Ticks slide, the index does not,
    /// and the letters tell you which way is north without needing a number read first.
    /// </summary>
    private void BuildCompass(Transform parent)
    {
        RectTransform group = NewGroup("Compass", parent, new Vector2(0.5f, 1f), new Vector2(0f, -CompassTop));

        // Two spare: the window can straddle one more tick boundary than it strictly
        // spans, depending on where between two ticks the current heading has landed.
        int count = Mathf.CeilToInt(compassRange / Mathf.Max(compassTickStep, 1f)) + 2;

        compassTicks = new PixelMark[count];
        for (int i = 0; i < count; i++)
            compassTicks[i] = new PixelMark("CompassTick", group, new Vector2(1f, 3f), markColor, trackColor, textShadow);

        compassCardinals = new PixelLabel[CardinalNames.Length];
        for (int i = 0; i < CardinalNames.Length; i++)
        {
            PixelLabel label = new PixelLabel("Cardinal" + CardinalNames[i], group, fillColor, trackColor, textShadow);
            Place(label.Root, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(0f, CardinalRow));
            label.Set(CardinalNames[i]);
            compassCardinals[i] = label;
        }

        // The index, and the only part of the compass that stands still. Whatever has
        // slid under it is where the nose is pointing.
        BuildMark("CompassIndexBar", group, new Vector2(-1f, 4f), new Vector2(3f, 1f));
        BuildMark("CompassIndexTip", group, new Vector2(0f, 3f), Vector2.one);

        if (showValues)
        {
            headingValue = new PixelLabel("HeadingValue", group, fillColor, trackColor, textShadow);
            Place(headingValue.Root, new Vector2(0.5f, 0.5f), Vector2.zero,
                new Vector2(-Mathf.Floor(PixelFont.TextWidth("000") * 0.5f), HeadingRow));
        }
    }

    /// <summary>A mark placed once and left there.</summary>
    private void BuildMark(string name, Transform parent, Vector2 position, Vector2 size)
    {
        new PixelMark(name, parent, size, fillColor, trackColor, textShadow).Place(position);
    }

    /// <summary>A sizeless rect used purely as an origin for the things hung off it.</summary>
    private static RectTransform NewGroup(string name, Transform parent, Vector2 anchor, Vector2 position)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);

        RectTransform rect = go.GetComponent<RectTransform>();
        Place(rect, anchor, new Vector2(0.5f, 0.5f), position);
        rect.sizeDelta = Vector2.zero;
        return rect;
    }

    /// <summary>A labelled horizontal bar that fills from the left.</summary>
    private RectTransform BuildBar(string name, Transform parent, string label, Vector2 position, out RectTransform track)
    {
        track = CreateImage(name + "Track", parent, trackColor);
        Place(track, Vector2.zero, Vector2.zero, position);
        track.sizeDelta = new Vector2(barWidth, barHeight);

        if (showLabels)
        {
            PixelLabel text = new PixelLabel(name + "Label", parent, fillColor, trackColor, textShadow);
            Place(text.Root, Vector2.zero, Vector2.zero, new Vector2(margin, position.y));
            text.Set(label);
        }

        RectTransform fill = CreateImage(name + "Fill", track, fillColor);
        fill.anchorMin = Vector2.zero;
        fill.anchorMax = new Vector2(0f, 1f);
        fill.pivot = Vector2.zero;
        fill.anchoredPosition = Vector2.zero;
        return fill;
    }

    private Canvas BuildCanvas()
    {
        GameObject go = new GameObject("GBA HUD Canvas", typeof(Canvas), typeof(CanvasScaler));
        go.transform.SetParent(transform, false);

        Canvas canvas = go.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceCamera;
        canvas.worldCamera = gbaCamera;
        canvas.sortingOrder = 100;

        // Sit just past the near plane so no scenery can ever poke through the HUD.
        if (gbaCamera != null)
            canvas.planeDistance = gbaCamera.nearClipPlane + 0.01f;

        // Constant pixel size at scale 1 against a 160x144 target means one canvas
        // unit is exactly one GBA pixel. No reference resolution needed.
        CanvasScaler scaler = go.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
        scaler.scaleFactor = 1f;

        return canvas;
    }

    private static void Place(RectTransform rect, Vector2 anchor, Vector2 pivot, Vector2 position)
    {
        rect.anchorMin = anchor;
        rect.anchorMax = anchor;
        rect.pivot = pivot;
        rect.anchoredPosition = position;
    }

    private static RectTransform CreateImage(string name, Transform parent, Color color)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);

        Image image = go.GetComponent<Image>();
        image.color = color;
        image.raycastTarget = false;   // nothing here is clickable

        return go.GetComponent<RectTransform>();
    }

    /// <summary>
    /// A solid quad carrying the same one pixel shadow the text does.
    ///
    /// Every moving mark on these two gauges spends half its life over open sky, and open
    /// sky is the lightest of the four colours the shader allows. A white pixel on it is
    /// not a pixel at all, so the dark copy underneath is not decoration -- it is the only
    /// reason the mark can be seen at all.
    /// </summary>
    private sealed class PixelMark
    {
        public PixelMark(string name, Transform parent, Vector2 size, Color ink, Color shadow, bool withShadow)
        {
            // Built first so it lands underneath, and offset the way a shadow falls.
            if (withShadow)
            {
                shadowRect = CreateImage(name + "Shadow", parent, shadow);
                Configure(shadowRect, size);
            }

            inkRect = CreateImage(name, parent, ink);
            Configure(inkRect, size);
        }

        public bool Visible
        {
            set
            {
                // Guarded, because this is called on every mark every frame and
                // SetActive on an unchanged value still walks the hierarchy.
                if (visible == value)
                    return;

                visible = value;
                inkRect.gameObject.SetActive(value);

                if (shadowRect != null)
                    shadowRect.gameObject.SetActive(value);
            }
        }

        /// <summary>Snapped to whole pixels, because half a pixel is where the shimmer lives.</summary>
        public void Place(Vector2 position)
        {
            Vector2 snapped = new Vector2(Mathf.Round(position.x), Mathf.Round(position.y));
            inkRect.anchoredPosition = snapped;

            if (shadowRect != null)
                shadowRect.anchoredPosition = snapped + new Vector2(1f, -1f);
        }

        private static void Configure(RectTransform rect, Vector2 size)
        {
            // Anchored to the middle of whatever group it belongs to, and pivoted at its
            // own corner so an integer position puts it on an exact pixel boundary.
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = Vector2.zero;
            rect.sizeDelta = size;
        }

        private readonly RectTransform inkRect;
        private readonly RectTransform shadowRect;
        private bool visible = true;
    }

    /// <summary>
    /// A short string as a row of 3x5 quads, optionally over a one pixel shadow.
    ///
    /// Glyph sprites are only ever swapped when the text actually changes, so a
    /// readout that ticks over once a second costs nothing on the frames in between.
    /// </summary>
    private sealed class PixelLabel
    {
        public RectTransform Root { get; }

        public bool Visible
        {
            get => Root.gameObject.activeSelf;
            set => Root.gameObject.SetActive(value);
        }

        public PixelLabel(string name, Transform parent, Color ink, Color shadow, bool withShadow)
        {
            inkColor = ink;
            shadowColor = shadow;

            Root = NewRect(name, parent);

            // Built first so it draws underneath, and offset down and right the way a
            // drop shadow falls.
            if (withShadow)
            {
                shadowLayer = NewRect("Shadow", Root);
                shadowLayer.anchoredPosition = new Vector2(1f, -1f);
            }

            inkLayer = NewRect("Ink", Root);
        }

        public void Set(string text)
        {
            if (text == current)
                return;

            current = text;

            if (shadowLayer != null)
                Fill(shadowLayer, shadowGlyphs, text, shadowColor);

            Fill(inkLayer, inkGlyphs, text, inkColor);
            Root.sizeDelta = new Vector2(PixelFont.TextWidth(text), PixelFont.Height);
        }

        private readonly RectTransform inkLayer;
        private readonly RectTransform shadowLayer;
        private readonly List<Image> inkGlyphs = new List<Image>(8);
        private readonly List<Image> shadowGlyphs = new List<Image>(8);
        private readonly Color inkColor;
        private readonly Color shadowColor;
        private string current;

        private static void Fill(RectTransform layer, List<Image> glyphs, string text, Color color)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (i == glyphs.Count)
                {
                    RectTransform rect = CreateImage("Glyph", layer, color);
                    rect.anchorMin = Vector2.zero;
                    rect.anchorMax = Vector2.zero;
                    rect.pivot = Vector2.zero;
                    rect.sizeDelta = new Vector2(PixelFont.Width, PixelFont.Height);
                    glyphs.Add(rect.GetComponent<Image>());
                }

                Image glyph = glyphs[i];
                glyph.rectTransform.anchoredPosition = new Vector2(i * (PixelFont.Width + PixelFont.Tracking), 0f);
                glyph.sprite = PixelFont.Glyph(text[i]);
                glyph.enabled = true;
            }

            // Kept around rather than destroyed, so a number shrinking from three digits
            // to two and back does not churn objects every second.
            for (int i = text.Length; i < glyphs.Count; i++)
                glyphs[i].enabled = false;
        }

        private static RectTransform NewRect(string name, Transform parent)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.zero;
            rect.pivot = Vector2.zero;
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = Vector2.zero;
            return rect;
        }
    }
}
