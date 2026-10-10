using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// A soft edge round map text so it reads over walls and the route: the glyphs again in the halo colour at
// eight offsets, under the text. One pass of nine copies, where three stacked Outlines made 125.
[RequireComponent(typeof(Graphic))]
public class TextHalo : BaseMeshEffect
{
    public Color color = Color.white;
    public float distance = 2f;

    static readonly List<UIVertex> s_Input = new List<UIVertex>();
    static readonly List<UIVertex> s_Output = new List<UIVertex>();

    const float Diagonal = 0.7071f;   // so the diagonal copies sit at distance d too

    public void SetColor(Color value)
    {
        if (value == color)
            return;
        color = value;
        if (graphic != null)
            graphic.SetVerticesDirty();
    }

    public override void ModifyMesh(VertexHelper vh)
    {
        if (!IsActive() || vh.currentVertCount == 0)
            return;

        s_Input.Clear();
        vh.GetUIVertexStream(s_Input);
        s_Output.Clear();

        float d = distance, e = distance * Diagonal;
        Copy(d, 0f);
        Copy(-d, 0f);
        Copy(0f, d);
        Copy(0f, -d);
        Copy(e, e);
        Copy(-e, e);
        Copy(e, -e);
        Copy(-e, -e);

        // The glyphs themselves last, on top.
        s_Output.AddRange(s_Input);

        vh.Clear();
        vh.AddUIVertexTriangleStream(s_Output);
    }

    // One copy of the glyphs in the halo colour, moved by (x, y). A fading text takes its halo with it.
    void Copy(float x, float y)
    {
        var offset = new Vector3(x, y, 0f);
        for (int i = 0; i < s_Input.Count; i++)
        {
            UIVertex v = s_Input[i];
            v.position += offset;
            Color32 c = color;
            c.a = (byte)(c.a * v.color.a / 255);
            v.color = c;
            s_Output.Add(v);
        }
    }
}
