using UnityEditor;
using UnityEditor.Android;
using UnityEditor.Build;
using UnityEngine;
using static UIBuild;

// The pictures, materials and meshes the scene uses, and the import settings it needs. Every importer is
// null-checked with an error naming the file, and a texture is reimported only when a setting is actually off.
public static partial class BuildNavigateScene
{
    // Uncompressed (they're small, and compression smears thin lines); mipmapped unless a UI sprite. Anisotropic
    // filtering keeps the route's chevrons sharp on the floor at a shallow angle.
    static bool ImportTextures()
    {
        bool ok = SetImport(PlanImagePath, false, false, 2048, 0);
        ok &= SetImport(UIFolder + "/Ring.png", false, false, 256, 4);
        ok &= SetImport(UIFolder + "/Beam.png", false, false, 256, 0);
        ok &= SetImport(UIFolder + "/RouteMask.png", true, false, 256, 8, sRGB: false);   // A mask, not a colour.
        ok &= SetSprite(UIFolder + "/Pin.png", 0);
        ok &= SetSprite(UIFolder + "/Cone.png", 0);
        ok &= SetSprite(UIFolder + "/Shadow.png", 64);
        if (!ok)
            return false;
        s_Shadow = AssetDatabase.LoadAssetAtPath<Sprite>(UIFolder + "/Shadow.png");
        s_PlanImage = AssetDatabase.LoadAssetAtPath<Texture2D>(PlanImagePath);
        s_RouteMask = UITexture("RouteMask.png");
        s_Pin = AssetDatabase.LoadAssetAtPath<Sprite>(UIFolder + "/Pin.png");
        s_Cone = AssetDatabase.LoadAssetAtPath<Sprite>(UIFolder + "/Cone.png");
        return true;
    }

    static TextureImporter Importer(string path, bool required)
    {
        if (AssetImporter.GetAtPath(path) is TextureImporter importer)
            return importer;
        if (required)
            Debug.LogError($"[Navigation] Missing {path}: make it with the scripts in Tools/ba1-floor1 (see the guide) and build again.");
        return null;
    }

    static bool SetImport(string path, bool repeat, bool sprite, int maxSize, int aniso, bool sRGB = true)
    {
        TextureImporter importer = Importer(path, true);
        if (importer == null)
            return false;
        TextureImporterType type = sprite ? TextureImporterType.Sprite : TextureImporterType.Default;
        TextureWrapMode wrap = repeat ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
        if (importer.textureType == type && importer.alphaIsTransparency && importer.mipmapEnabled == !sprite
            && importer.wrapMode == wrap && importer.filterMode == FilterMode.Trilinear
            && importer.anisoLevel == aniso && importer.maxTextureSize == maxSize
            && importer.textureCompression == TextureImporterCompression.Uncompressed
            && importer.sRGBTexture == sRGB)
            return true;
        importer.textureType = type;
        importer.alphaIsTransparency = true;
        importer.mipmapEnabled = !sprite;
        importer.wrapMode = wrap;
        importer.filterMode = FilterMode.Trilinear;
        importer.anisoLevel = aniso;
        importer.maxTextureSize = maxSize;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.sRGBTexture = sRGB;
        importer.SaveAndReimport();
        return true;
    }

    // A UI sprite; border: its 9-slice border on every side, in pixels.
    static bool SetSprite(string path, int border)
    {
        TextureImporter importer = Importer(path, true);
        if (importer == null)
            return false;
        var borders = new Vector4(border, border, border, border);
        if (importer.textureType == TextureImporterType.Sprite && importer.spriteImportMode == SpriteImportMode.Single
            && !importer.mipmapEnabled && importer.wrapMode == TextureWrapMode.Clamp
            && importer.filterMode == FilterMode.Bilinear
            && importer.textureCompression == TextureImporterCompression.Uncompressed
            && importer.spriteBorder == borders && importer.alphaIsTransparency)
            return true;
        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.mipmapEnabled = false;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.filterMode = FilterMode.Bilinear;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.spriteBorder = borders;
        importer.alphaIsTransparency = true;
        importer.SaveAndReimport();
        return true;
    }

    // The floor map's picture: big, so compressed, and mipmapped since it's mostly shown smaller than it is.
    // Reimported only if its settings are off: it's slow to compress.
    static Texture2D ImportBasemap(string theme)
    {
        string path = $"{DataFolder}/ba1-floor1-map-{theme}.png";
        TextureImporter importer = Importer(path, false);
        if (importer == null)
            return null;
        if (importer.textureType != TextureImporterType.Default || importer.npotScale != TextureImporterNPOTScale.None
            || !importer.mipmapEnabled || importer.mipmapFilter != TextureImporterMipFilter.KaiserFilter
            || importer.wrapMode != TextureWrapMode.Clamp || importer.filterMode != FilterMode.Trilinear
            || importer.anisoLevel != 1 || importer.maxTextureSize != 4096
            || importer.textureCompression != TextureImporterCompression.CompressedHQ)
        {
            importer.textureType = TextureImporterType.Default;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.mipmapEnabled = true;
            importer.mipmapFilter = TextureImporterMipFilter.KaiserFilter;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Trilinear;
            importer.anisoLevel = 1;
            importer.maxTextureSize = 4096;
            importer.textureCompression = TextureImporterCompression.CompressedHQ;
            importer.SaveAndReimport();
        }
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    // The launch screen's logo: mipmapped, as it's drawn smaller than it is, and compressed (high quality: it's a
    // smooth picture, and both themes' copies are loaded with the scene). Optional.
    static Texture2D ImportLogo(string theme)
    {
        string path = $"{UIFolder}/Logo-{theme}.png";
        TextureImporter importer = Importer(path, false);
        if (importer == null)
        {
            Debug.LogWarning($"[Navigation] Missing {path}: the launch screen has no logo.");
            return null;
        }
        if (importer.textureType != TextureImporterType.Default || importer.npotScale != TextureImporterNPOTScale.None
            || !importer.mipmapEnabled || !importer.alphaIsTransparency || importer.wrapMode != TextureWrapMode.Clamp
            || importer.filterMode != FilterMode.Trilinear
            || importer.textureCompression != TextureImporterCompression.CompressedHQ)
        {
            importer.textureType = TextureImporterType.Default;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.mipmapEnabled = true;
            importer.alphaIsTransparency = true;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Trilinear;
            importer.textureCompression = TextureImporterCompression.CompressedHQ;
            importer.SaveAndReimport();
        }
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    // The marker shapes, as sprites: mipmapped, since they're drawn at under half their size. Null if any is missing.
    static Sprite[] ImportMarkers()
    {
        var sprites = new Sprite[MarkerShapes.Length];
        for (int i = 0; i < MarkerShapes.Length; i++)
        {
            string path = $"{MarkerFolder}/marker-{MarkerShapes[i]}.png";
            TextureImporter importer = Importer(path, true);
            if (importer == null)
                return null;
            if (importer.textureType != TextureImporterType.Sprite || importer.spriteImportMode != SpriteImportMode.Single
                || !importer.alphaIsTransparency || !importer.mipmapEnabled || importer.wrapMode != TextureWrapMode.Clamp
                || importer.filterMode != FilterMode.Trilinear
                || importer.textureCompression != TextureImporterCompression.Uncompressed)
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.alphaIsTransparency = true;
                importer.mipmapEnabled = true;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.filterMode = FilterMode.Trilinear;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.SaveAndReimport();
            }
            sprites[i] = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (sprites[i] == null)
            {
                Debug.LogError($"[Navigation] {path} didn't import as a sprite.");
                return null;
            }
        }
        return sprites;
    }

    // The launcher icon: an adaptive icon (a door on the theme's background), a round one and a square one.
    // The same 432 px picture goes in every density slot; Unity scales it.
    static void SetAppIcons(string theme)
    {
        string[] parts = { "back", "door", "round", "square" };
        var icons = new Texture2D[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            string path = $"{UIFolder}/Icon-{theme}-{parts[i]}.png";
            TextureImporter importer = Importer(path, false);
            if (importer == null)
            {
                Debug.LogWarning($"[Navigation] Missing app icons for {theme} in {UIFolder}; keeping the current ones.");
                return;
            }
            if (importer.textureType != TextureImporterType.Default || importer.mipmapEnabled
                || importer.npotScale != TextureImporterNPOTScale.None
                || importer.textureCompression != TextureImporterCompression.Uncompressed)
            {
                importer.textureType = TextureImporterType.Default;
                importer.mipmapEnabled = false;
                importer.npotScale = TextureImporterNPOTScale.None;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.SaveAndReimport();
            }
            icons[i] = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        SetIcons(AndroidPlatformIconKind.Adaptive, icons[0], icons[1]);   // background, then foreground
        // Unity 6 marks these obsolete, but the app still installs on Android 7.1 (API 25), which has no adaptive
        // icons: without them its launchers would show Unity's default.
#pragma warning disable CS0618
        SetIcons(AndroidPlatformIconKind.Round, icons[2]);
        SetIcons(AndroidPlatformIconKind.Legacy, icons[3]);
#pragma warning restore CS0618
    }

    static void SetIcons(PlatformIconKind kind, params Texture2D[] layers)
    {
        PlatformIcon[] icons = PlayerSettings.GetPlatformIcons(NamedBuildTarget.Android, kind);
        foreach (PlatformIcon icon in icons)
            icon.SetTextures(layers);
        PlayerSettings.SetPlatformIcons(NamedBuildTarget.Android, kind, icons);
    }

    // Material assets, so their shaders are included in the build (a shader only found with Shader.Find
    // wouldn't be). Made if missing; the shader is set every time.
    static Material MaterialAsset(string name, Shader shader)
    {
        EnsureFolder(MaterialFolder);
        string path = $"{MaterialFolder}/{name}.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(shader);
            AssetDatabase.CreateAsset(material, path);
        }
        material.shader = shader;
        EditorUtility.SetDirty(material);
        return material;
    }

    static Material ScrollMaterial(string name, Texture texture, Color color, float speed, int queue)
    {
        Material material = MaterialAsset(name, s_Shader);
        material.mainTexture = texture;
        material.color = color;
        material.SetFloat("_ScrollSpeed", speed);
        material.renderQueue = queue;
        return material;
    }

    // A 1 x 1 quad in the XY plane facing -Z, white vertex colors (the shader multiplies by them).
    // bottom: where its lower edge is; 0 puts the pivot at the bottom middle.
    static Mesh QuadMesh(string name, float bottom)
    {
        EnsureFolder(MeshFolder);
        string path = $"{MeshFolder}/{name}.asset";
        var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        if (mesh == null)
        {
            mesh = new Mesh { name = name };
            AssetDatabase.CreateAsset(mesh, path);
        }
        mesh.Clear();
        mesh.vertices = new[]
        {
            new Vector3(-0.5f, bottom, 0f), new Vector3(-0.5f, bottom + 1f, 0f),
            new Vector3(0.5f, bottom + 1f, 0f), new Vector3(0.5f, bottom, 0f),
        };
        mesh.uv = new[] { new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f) };
        var white = new Color32(255, 255, 255, 255);
        mesh.colors32 = new[] { white, white, white, white };
        mesh.triangles = new[] { 0, 1, 2, 2, 3, 0 };
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        EditorUtility.SetDirty(mesh);
        return mesh;
    }
}
