using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A 3x5 bitmap font, built in code, for a screen 160 pixels across.
///
/// No TrueType font survives this. A vector font rasterised at six or eight points is
/// antialiased into grey fringes, the render texture is point sampled so nothing
/// smooths those fringes back out, and then the palette shader rounds every one of
/// them to whichever of four colours it lands nearest -- which is how you get text
/// that shimmers and breaks up as the camera moves. Hand-placed pixels have none of
/// those problems, because there is nothing between on and off to round.
///
/// Three by five is the smallest grid an alphabet fits on. It is the size Game Boy
/// menus used, and at this resolution it buys you 32 characters across the screen.
///
/// Glyphs are drawn into a texture per character and handed out as sprites, so a
/// label is a row of 3x5 quads and a changing number only ever swaps the sprite on
/// the digits that actually changed.
/// </summary>
public static class PixelFont
{
    public const int Width = 3;
    public const int Height = 5;
    /// <summary>Blank columns between one glyph and the next.</summary>
    public const int Tracking = 1;

    /// <summary>How wide a string comes out, in screen pixels.</summary>
    public static int TextWidth(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        return text.Length * Width + (text.Length - 1) * Tracking;
    }

    /// <summary>
    /// A single glyph as a 3x5 sprite, one texture pixel to one screen pixel. Cached,
    /// so the whole font is at most a few dozen tiny textures however much text is drawn.
    /// </summary>
    public static Sprite Glyph(char c)
    {
        c = char.ToUpperInvariant(c);
        if (!Shapes.ContainsKey(c))
            c = '?';

        if (cache.TryGetValue(c, out Sprite cached) && cached != null)
            return cached;

        Sprite built = Build(Shapes[c]);
        cache[c] = built;
        return built;
    }

    private static readonly Dictionary<char, Sprite> cache = new Dictionary<char, Sprite>(48);

    // Static state outlives a play session in the editor, but the textures behind these
    // sprites do not. Clearing on load stops the second run rendering with a cache full
    // of destroyed objects.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ClearCache() => cache.Clear();

    private static Sprite Build(string shape)
    {
        Texture2D texture = new Texture2D(Width, Height, TextureFormat.RGBA32, false)
        {
            name = "PixelFont Glyph",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave
        };

        Color32 ink = new Color32(255, 255, 255, 255);
        Color32 blank = new Color32(255, 255, 255, 0);
        Color32[] pixels = new Color32[Width * Height];

        for (int row = 0; row < Height; row++)
            for (int column = 0; column < Width; column++)
                // Shapes read top down the way they are written; textures run bottom up.
                pixels[(Height - 1 - row) * Width + column] =
                    shape[row * Width + column] == '#' ? ink : blank;

        texture.SetPixels32(pixels);
        texture.Apply(false, true);

        // Pixels per unit of 1 makes one texture pixel one canvas unit, which on a
        // canvas scaled 1:1 to a 160x144 target is one screen pixel.
        Sprite sprite = Sprite.Create(texture, new Rect(0f, 0f, Width, Height),
            Vector2.zero, 1f, 0, SpriteMeshType.FullRect);
        sprite.hideFlags = HideFlags.HideAndDontSave;
        return sprite;
    }

    // Five rows of three, written out so the shape is the source. Some letters are a
    // compromise at this size -- M and N have to share a trick, and W is a wide V --
    // but every one of them is legible at a glance, which is the only test that counts.
    private static readonly Dictionary<char, string> Shapes = new Dictionary<char, string>
    {
        { ' ', "..." + "..." + "..." + "..." + "..." },
        { '?', "###" + "..#" + ".##" + "..." + ".#." },
        { '-', "..." + "..." + "###" + "..." + "..." },
        { '.', "..." + "..." + "..." + "..." + ".#." },
        { ':', "..." + ".#." + "..." + ".#." + "..." },
        { '/', "..#" + "..#" + ".#." + "#.." + "#.." },
        { '%', "#.#" + "..#" + ".#." + "#.." + "#.#" },
        { '+', "..." + ".#." + "###" + ".#." + "..." },
        { '!', ".#." + ".#." + ".#." + "..." + ".#." },

        { '0', "###" + "#.#" + "#.#" + "#.#" + "###" },
        { '1', ".#." + "##." + ".#." + ".#." + "###" },
        { '2', "###" + "..#" + "###" + "#.." + "###" },
        { '3', "###" + "..#" + "###" + "..#" + "###" },
        { '4', "#.#" + "#.#" + "###" + "..#" + "..#" },
        { '5', "###" + "#.." + "###" + "..#" + "###" },
        { '6', "###" + "#.." + "###" + "#.#" + "###" },
        { '7', "###" + "..#" + "..#" + "..#" + "..#" },
        { '8', "###" + "#.#" + "###" + "#.#" + "###" },
        { '9', "###" + "#.#" + "###" + "..#" + "###" },

        { 'A', "###" + "#.#" + "###" + "#.#" + "#.#" },
        { 'B', "##." + "#.#" + "##." + "#.#" + "##." },
        { 'C', "###" + "#.." + "#.." + "#.." + "###" },
        { 'D', "##." + "#.#" + "#.#" + "#.#" + "##." },
        { 'E', "###" + "#.." + "##." + "#.." + "###" },
        { 'F', "###" + "#.." + "##." + "#.." + "#.." },
        { 'G', "###" + "#.." + "#.#" + "#.#" + "###" },
        { 'H', "#.#" + "#.#" + "###" + "#.#" + "#.#" },
        { 'I', "###" + ".#." + ".#." + ".#." + "###" },
        { 'J', "..#" + "..#" + "..#" + "#.#" + "###" },
        { 'K', "#.#" + "#.#" + "##." + "#.#" + "#.#" },
        { 'L', "#.." + "#.." + "#.." + "#.." + "###" },
        { 'M', "#.#" + "###" + "###" + "#.#" + "#.#" },
        { 'N', "#.#" + "##." + "###" + ".##" + "#.#" },
        { 'O', "###" + "#.#" + "#.#" + "#.#" + "###" },
        { 'P', "###" + "#.#" + "###" + "#.." + "#.." },
        { 'Q', "###" + "#.#" + "#.#" + "###" + "..#" },
        { 'R', "###" + "#.#" + "##." + "#.#" + "#.#" },
        { 'S', "###" + "#.." + "###" + "..#" + "###" },
        { 'T', "###" + ".#." + ".#." + ".#." + ".#." },
        { 'U', "#.#" + "#.#" + "#.#" + "#.#" + "###" },
        { 'V', "#.#" + "#.#" + "#.#" + "#.#" + ".#." },
        { 'W', "#.#" + "#.#" + "###" + "###" + "#.#" },
        { 'X', "#.#" + "#.#" + ".#." + "#.#" + "#.#" },
        { 'Y', "#.#" + "#.#" + ".#." + ".#." + ".#." },
        { 'Z', "###" + "..#" + ".#." + "#.." + "###" },
    };
}
