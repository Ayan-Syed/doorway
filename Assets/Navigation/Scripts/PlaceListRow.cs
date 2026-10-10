using UnityEngine;
using UnityEngine.UI;

// One row of a PlaceList: the whole row is the button, with the place's name and what it is under it.
// (v7 also had an icon disc and a right-hand action button that the builder never made; they were left out.)
public class PlaceListRow : MonoBehaviour
{
    public Button button;
    public Text title;
    public Text detail;
}
