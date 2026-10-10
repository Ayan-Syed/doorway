using UnityEngine;
using UnityEngine.UI;

// One row of a PlaceList: the whole row is the button, with the place's name and what it is under it.
// (v7 also had an icon disc and a right-hand action button that the builder never made; they were left out.)
public class PlaceListRow : MonoBehaviour
{
    public Button button;
    public Text title;
    public Text detail;
    [Tooltip("The title and detail's column: indented on a row nested under another.")]
    public RectTransform textColumn;
    public RectTransform divider;
    [Tooltip("\"6 inside\" and a chevron, on a row with rooms inside it: shows or hides them. Off otherwise.")]
    public Button toggle;
    public Text toggleLabel;
    public Text toggleIcon;
}
