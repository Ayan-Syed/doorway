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

    public void Apply(bool night)
    {
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

        Graphic[] from = template.GetComponentsInChildren<Graphic>(true);
        Graphic[] to = copy.GetComponentsInChildren<Graphic>(true);
        for (int i = 0; i < from.Length && i < to.Length; i++)
            to[i].color = from[i].color;

        TextHalo[] haloFrom = template.GetComponentsInChildren<TextHalo>(true);
        TextHalo[] haloTo = copy.GetComponentsInChildren<TextHalo>(true);
        for (int i = 0; i < haloFrom.Length && i < haloTo.Length; i++)
            haloTo[i].SetColor(haloFrom[i].color);
    }
}
