using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// Day and night colours for everything the scene builder made, so Settings can switch between them
// without a rebuild. The builder makes the screen in both themes and records here only what differs:
// colours, the map's picture, text halos, the search fields' caret, and the walls.
public class ThemeColors : MonoBehaviour
{
    [Serializable]
    public struct GraphicColor
    {
        public Graphic graphic;
        public Color day;
        public Color night;
    }

    [Serializable]
    public struct TextureSwap
    {
        public RawImage image;
        public Texture day;
        public Texture night;
    }

    [Serializable]
    public struct HaloColor
    {
        public TextHalo halo;
        public Color day;
        public Color night;
    }

    [Serializable]
    public struct FieldColor
    {
        public InputField field;
        public Color dayCaret, nightCaret, daySelection, nightSelection;
    }

    [Serializable]
    public struct WallColor
    {
        public WallMesh wall;
        public Color dayTop, nightTop, daySide, nightSide;
    }

    public List<GraphicColor> graphics = new List<GraphicColor>();
    public List<TextureSwap> textures = new List<TextureSwap>();
    public List<HaloColor> halos = new List<HaloColor>();
    public List<FieldColor> fields = new List<FieldColor>();
    public List<WallColor> walls = new List<WallColor>();

    // CopyColors' template, its parts kept for the rest of a restyle: every row or label made from one template is
    // recoloured in a row, so the template's parts are found once, not once per copy. The copy's go in buffers.
    static Component s_Template;
    static Graphic[] s_TemplateGraphics;
    static TextHalo[] s_TemplateHalos;
    static readonly List<Graphic> s_Graphics = new List<Graphic>();
    static readonly List<TextHalo> s_Halos = new List<TextHalo>();

    public void Apply(bool night)
    {
        s_Template = null;   // a new restyle: look at the templates afresh

        foreach (GraphicColor g in graphics)
            if (g.graphic != null)
                g.graphic.color = night ? g.night : g.day;

        foreach (TextureSwap t in textures)
            if (t.image != null)
                t.image.texture = night ? t.night : t.day;

        foreach (HaloColor h in halos)
            if (h.halo != null)
                h.halo.SetColor(night ? h.night : h.day);

        foreach (FieldColor f in fields)
        {
            if (f.field == null)
                continue;
            f.field.caretColor = night ? f.nightCaret : f.dayCaret;
            f.field.selectionColor = night ? f.nightSelection : f.daySelection;
        }

        foreach (WallColor w in walls)
            if (w.wall != null)
                w.wall.SetColors(night ? w.nightTop : w.dayTop, night ? w.nightSide : w.daySide);
    }

    // A copy made at runtime (a list row, a room number) takes its template's colours again after a
    // switch. The two must have the same parts in the same order.
    public static void CopyColors(Component template, Component copy)
    {
        if (template == null || copy == null)
            return;
        if (template != s_Template)
        {
            s_Template = template;
            s_TemplateGraphics = template.GetComponentsInChildren<Graphic>(true);
            s_TemplateHalos = template.GetComponentsInChildren<TextHalo>(true);
        }

        copy.GetComponentsInChildren(true, s_Graphics);
        for (int i = 0; i < s_TemplateGraphics.Length && i < s_Graphics.Count; i++)
            s_Graphics[i].color = s_TemplateGraphics[i].color;

        copy.GetComponentsInChildren(true, s_Halos);
        for (int i = 0; i < s_TemplateHalos.Length && i < s_Halos.Count; i++)
            s_Halos[i].SetColor(s_TemplateHalos[i].color);
        s_Graphics.Clear();
        s_Halos.Clear();
    }

    // Stops switching an image's picture between day and night, and says which pictures it had (the launch logo,
    // once the app has opened, so they can leave memory).
    public void Forget(RawImage image, List<Texture> pictures)
    {
        for (int i = textures.Count - 1; i >= 0; i--)
        {
            if (textures[i].image != image)
                continue;
            pictures.Add(textures[i].day);
            pictures.Add(textures[i].night);
            textures.RemoveAt(i);
        }
    }
}
