using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

// The small layout helpers every screen in the scene builder is made of, and the shared style: the fonts and the
// one rounded sprite that draws every card, pill and dot. Coordinates are uGUI reference pixels on a 1080-wide
// canvas. Every Image and Text starts with raycasts off; only the button helpers turn them back on.
internal static class UIBuild
{
    internal const string MaterialFolder = "Assets/Navigation/Materials";
    internal const string FontFolder = "Assets/Navigation/Fonts";
    internal const string UIFolder = "Assets/Navigation/UI";
    internal const string RoundedSpritePath = UIFolder + "/Rounded.png";

    // The corner radius of Rounded.png in pixels, which is also its 9-slice border.
    internal const int SpriteRadius = 64;

    internal static Font s_Regular, s_SemiBold, s_Bold, s_Icons;
    internal static Sprite s_Rounded;

    // A bare RectTransform under parent: default (centred) anchors, 100 x 100.
    internal static RectTransform Child(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rect = (RectTransform)go.transform;
        rect.SetParent(parent, false);
        return rect;
    }

    // Fills the parent, inset by the margins.
    internal static RectTransform Stretch(string name, Transform parent, float l = 0f, float r = 0f, float t = 0f, float b = 0f)
    {
        RectTransform rect = Child(name, parent);
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(l, b);
        rect.offsetMax = new Vector2(-r, -t);
        return rect;
    }

    // The parent's full width less the margins, height tall, top px below the parent's top edge.
    internal static RectTransform TopBand(string name, Transform parent, float top, float height, float left, float right)
    {
        RectTransform rect = Child(name, parent);
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(left, -(top + height));
        rect.offsetMax = new Vector2(-right, -top);
        return rect;
    }

    // Anchored at one point of the parent. The pivot stays in the middle, so pos is where the centre goes;
    // a caller that moves the pivot afterwards makes pos where the pivot goes.
    internal static RectTransform Fixed(string name, Transform parent, Vector2 anchor, Vector2 pos, Vector2 size)
    {
        RectTransform rect = Child(name, parent);
        rect.anchorMin = anchor;
        rect.anchorMax = anchor;
        rect.anchoredPosition = pos;
        rect.sizeDelta = size;
        return rect;
    }

    // The rounded sprite, 9-sliced. Each Image picks its own radius with pixelsPerUnitMultiplier: the sprite's
    // 64 px corner draws radius units wide (both the sprite and the canvas keep 100 pixels per unit).
    internal static Image Rounded(RectTransform rect, Color color, float radius)
    {
        var image = rect.gameObject.AddComponent<Image>();
        image.sprite = s_Rounded;
        image.type = Image.Type.Sliced;
        image.pixelsPerUnitMultiplier = SpriteRadius / Mathf.Max(radius, 0.01f);
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    internal static Text TextIn(RectTransform rect, string content, Font font, int size, Color color, TextAnchor alignment)
    {
        var text = rect.gameObject.AddComponent<Text>();
        text.font = font;
        text.fontSize = size;
        text.color = color;
        text.alignment = alignment;
        text.raycastTarget = false;
        text.text = content;
        return text;
    }

    // A text sized by its parent's layout group: named after what it says, or "Icon" for a single glyph.
    internal static Text LayoutText(Transform parent, string content, Font font, int size, Color color)
    {
        string name = content != null && content.Length == 1 ? "Icon" : content ?? "";
        return TextIn(Child(name, parent), content ?? "", font, size, color, TextAnchor.MiddleCenter);
    }

    // Shrinks to fit, down to min; never grows past the size it was given.
    internal static void BestFit(Text text, int min)
    {
        text.resizeTextForBestFit = true;
        text.resizeTextMinSize = min;
        text.resizeTextMaxSize = text.fontSize;
    }

    // A rounded button. Dimming for disabled buttons is done with a CanvasGroup (NavigationScreen sets its
    // alpha), so the label dims too. Touch has no hover, and a tapped button shouldn't stay highlighted:
    // every state is white but pressed.
    internal static Button MakeButton(RectTransform rect, Color color, float radius, out Image bg)
    {
        bg = Rounded(rect, color, radius);
        bg.raycastTarget = true;
        var button = rect.gameObject.AddComponent<Button>();
        button.targetGraphic = bg;
        ColorBlock colors = button.colors;
        colors.normalColor = Color.white;
        colors.highlightedColor = Color.white;
        colors.selectedColor = Color.white;
        colors.disabledColor = Color.white;
        colors.pressedColor = new Color(0.78f, 0.78f, 0.78f);
        colors.colorMultiplier = 1f;
        colors.fadeDuration = 0.06f;
        button.colors = colors;
        button.navigation = new Navigation { mode = Navigation.Mode.None };
        rect.gameObject.AddComponent<CanvasGroup>();
        rect.gameObject.AddComponent<PressFeedback>();
        return button;
    }

    // Creates folder and any missing parents.
    internal static void EnsureFolder(string folder)
    {
        if (AssetDatabase.IsValidFolder(folder))
            return;
        string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
    }

    // The rounded sprite, Inter and Material Icons. False (and why) if any is missing.
    internal static bool LoadStyle()
    {
        s_Rounded = RoundedSprite();
        s_Regular = AssetDatabase.LoadAssetAtPath<Font>(FontFolder + "/Inter-400.ttf");
        s_SemiBold = AssetDatabase.LoadAssetAtPath<Font>(FontFolder + "/Inter-600.ttf");
        s_Bold = AssetDatabase.LoadAssetAtPath<Font>(FontFolder + "/Inter-700.ttf");
        s_Icons = AssetDatabase.LoadAssetAtPath<Font>(FontFolder + "/MaterialIcons-Regular.ttf");
        if (s_Rounded == null || s_Regular == null || s_SemiBold == null || s_Bold == null || s_Icons == null)
        {
            Debug.LogError($"[Navigation] Missing UI assets: the fonts in {FontFolder} or {RoundedSpritePath}.");
            return false;
        }
        return true;
    }

    // A white rounded square with anti-aliased corners, imported as a 9-sliced sprite, so one image draws cards,
    // buttons, pills and dots of any size and corner radius. Drawn here only if the file is missing.
    internal static Sprite RoundedSprite()
    {
        if (!File.Exists(RoundedSpritePath))
        {
            const int size = SpriteRadius * 2 + 32;
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                // Distance from the pixel centre to the rounded rectangle's edge (negative inside).
                var p = new Vector2(x + 0.5f, y + 0.5f);
                var nearest = new Vector2(Mathf.Clamp(p.x, SpriteRadius, size - SpriteRadius),
                                          Mathf.Clamp(p.y, SpriteRadius, size - SpriteRadius));
                float outside = Vector2.Distance(p, nearest) - SpriteRadius;
                pixels[y * size + x] = new Color32(255, 255, 255,
                    (byte)Mathf.RoundToInt(Mathf.Clamp01(0.5f - outside) * 255f));
            }
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.SetPixels32(pixels);
            texture.Apply();
            EnsureFolder(UIFolder);
            File.WriteAllBytes(RoundedSpritePath, texture.EncodeToPNG());
            Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(RoundedSpritePath, ImportAssetOptions.ForceSynchronousImport);
        }

        // Sprite, Single, the 64 px border on every side, no mipmaps, uncompressed. Reimported only if off.
        if (AssetImporter.GetAtPath(RoundedSpritePath) is TextureImporter importer)
        {
            var border = new Vector4(SpriteRadius, SpriteRadius, SpriteRadius, SpriteRadius);
            if (importer.textureType != TextureImporterType.Sprite
                || importer.spriteImportMode != SpriteImportMode.Single
                || importer.spriteBorder != border
                || !importer.alphaIsTransparency
                || importer.mipmapEnabled
                || importer.textureCompression != TextureImporterCompression.Uncompressed)
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.spriteBorder = border;
                importer.alphaIsTransparency = true;
                importer.mipmapEnabled = false;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.SaveAndReimport();
            }
        }
        return AssetDatabase.LoadAssetAtPath<Sprite>(RoundedSpritePath);
    }
}
