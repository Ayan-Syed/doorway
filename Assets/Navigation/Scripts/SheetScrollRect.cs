using UnityEngine.EventSystems;
using UnityEngine.UI;

// A list inside a bottom sheet. A drag moves the sheet instead of the list until the sheet is all the way up,
// and a drag down on a list already at its top moves the sheet down.
public class SheetScrollRect : ScrollRect
{
    public BottomSheet sheet;

    bool m_Sheet;   // this drag moves the sheet, decided once when it begins

    public override void OnBeginDrag(PointerEventData e)
    {
        bool atTop = content == null || content.anchoredPosition.y <= 1f;
        m_Sheet = sheet != null && sheet.draggable && sheet.stops.Length > 1 && (!sheet.IsAtTop || atTop && e.delta.y < 0f);
        if (m_Sheet)
            sheet.OnBeginDrag(e);
        else
            base.OnBeginDrag(e);
    }

    public override void OnDrag(PointerEventData e)
    {
        if (m_Sheet)
            sheet.OnDrag(e);
        else
            base.OnDrag(e);
    }

    public override void OnEndDrag(PointerEventData e)
    {
        if (m_Sheet)
            sheet.OnEndDrag(e);
        else
            base.OnEndDrag(e);
        m_Sheet = false;
    }
}
