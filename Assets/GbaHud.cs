using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Altitude and speed gauges drawn at native GBA resolution.
///
/// Builds its own canvas in Screen Space - Camera mode on the camera that renders
/// into the 160x144 render texture, so the HUD is captured by that texture and goes
/// through the palette shader with everything else. One canvas unit is one GBA pixel,
/// so every size below is literally in screen pixels.
///
/// Everything is drawn with plain Images and no sprite, which uGUI renders as a solid
/// quad. That keeps the gauges to hard-edged rectangles and needs no art assets.
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

    [Header("Speed gauge")]
    [SerializeField] private int speedTrackWidth = 56;
    [SerializeField] private int speedTrackHeight = 5;
    [Tooltip("Airspeed at which the bar is full. Set this near the plane's top speed.")]
    [SerializeField] private float maxDisplaySpeed = 30f;

    [Header("Layout")]
    [Tooltip("Gap in pixels between the gauges and the edge of the screen.")]
    [SerializeField] private int margin = 6;

    [Header("Palette")]
    [SerializeField] private Color trackColor = new Color(0.10f, 0.15f, 0.10f, 1f);
    [SerializeField] private Color fillColor = new Color(0.75f, 0.85f, 0.30f, 1f);
    [Tooltip("The y = 0 marker, and the bar when the plane drops below it.")]
    [SerializeField] private Color zeroColor = new Color(1f, 1f, 1f, 1f);

    private RectTransform altitudeFill;
    private RectTransform speedFill;
    private Image altitudeFillImage;

    private void Awake()
    {
        if (plane == null)
            plane = FindFirstObjectByType<AirshipController>();

        if (gbaCamera == null)
            gbaCamera = Camera.main;

        Build();
    }

    private void Update()
    {
        if (plane == null)
            return;

        UpdateAltitude(plane.transform.position.y);
        UpdateSpeed(plane.Airspeed);
    }

    private void UpdateAltitude(float altitude)
    {
        float half = altitudeTrackHeight * 0.5f;
        float normalized = Mathf.Clamp(altitude / Mathf.Max(maxDisplayAltitude, 0.01f), -1f, 1f);

        // The bar grows out of the middle of the track, so the y = 0 line stays put
        // and the fill reads as distance from it. Flipping the pivot is what lets the
        // same rect grow upward when climbing and downward when below sea level.
        altitudeFill.pivot = new Vector2(0.5f, normalized >= 0f ? 0f : 1f);
        altitudeFill.sizeDelta = new Vector2(0f, Mathf.Abs(normalized) * half);

        // Below zero reads as a warning, so it swaps to the marker colour.
        altitudeFillImage.color = normalized >= 0f ? fillColor : zeroColor;
    }

    private void UpdateSpeed(float airspeed)
    {
        float normalized = Mathf.Clamp01(airspeed / Mathf.Max(maxDisplaySpeed, 0.01f));

        // Round to whole pixels, or the end of the bar shimmers between two columns
        // as the speed drifts, which at this resolution is very obvious.
        speedFill.sizeDelta = new Vector2(Mathf.Round(normalized * speedTrackWidth), 0f);
    }

    // --- Construction ------------------------------------------------------

    private void Build()
    {
        Canvas canvas = BuildCanvas();

        // Altitude: a vertical track against the right edge, centred on screen.
        RectTransform altitudeTrack = CreateImage("AltitudeTrack", canvas.transform, trackColor);
        altitudeTrack.anchorMin = new Vector2(1f, 0.5f);
        altitudeTrack.anchorMax = new Vector2(1f, 0.5f);
        altitudeTrack.pivot = new Vector2(1f, 0.5f);
        altitudeTrack.anchoredPosition = new Vector2(-margin, 0f);
        altitudeTrack.sizeDelta = new Vector2(altitudeTrackWidth, altitudeTrackHeight);

        // Anchored to the track's middle and stretched to its width, so only the
        // height below has to change at runtime.
        altitudeFill = CreateImage("AltitudeFill", altitudeTrack, fillColor);
        altitudeFill.anchorMin = new Vector2(0f, 0.5f);
        altitudeFill.anchorMax = new Vector2(1f, 0.5f);
        altitudeFill.anchoredPosition = Vector2.zero;
        altitudeFillImage = altitudeFill.GetComponent<Image>();

        // Added after the fill so it draws over the top of it.
        RectTransform zeroLine = CreateImage("ZeroLine", altitudeTrack, zeroColor);
        zeroLine.anchorMin = new Vector2(0f, 0.5f);
        zeroLine.anchorMax = new Vector2(1f, 0.5f);
        zeroLine.pivot = new Vector2(0.5f, 0.5f);
        zeroLine.anchoredPosition = Vector2.zero;
        zeroLine.sizeDelta = new Vector2(4f, 1f);   // two pixels proud of the track on each side

        // Speed: a horizontal bar in the bottom left.
        RectTransform speedTrack = CreateImage("SpeedTrack", canvas.transform, trackColor);
        speedTrack.anchorMin = Vector2.zero;
        speedTrack.anchorMax = Vector2.zero;
        speedTrack.pivot = Vector2.zero;
        speedTrack.anchoredPosition = new Vector2(margin, margin);
        speedTrack.sizeDelta = new Vector2(speedTrackWidth, speedTrackHeight);

        speedFill = CreateImage("SpeedFill", speedTrack, fillColor);
        speedFill.anchorMin = Vector2.zero;
        speedFill.anchorMax = new Vector2(0f, 1f);
        speedFill.pivot = Vector2.zero;
        speedFill.anchoredPosition = Vector2.zero;
    }

    private Canvas BuildCanvas()
    {
        GameObject go = new GameObject("GBA HUD Canvas",
            typeof(Canvas), typeof(CanvasScaler));
        go.transform.SetParent(transform, false);

        Canvas canvas = go.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceCamera;
        canvas.worldCamera = gbaCamera;

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

    private static RectTransform CreateImage(string name, Transform parent, Color color)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);

        Image image = go.GetComponent<Image>();
        image.color = color;
        image.raycastTarget = false;   // nothing here is clickable

        return go.GetComponent<RectTransform>();
    }
}
