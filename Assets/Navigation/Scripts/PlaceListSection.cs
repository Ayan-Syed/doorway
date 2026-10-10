using UnityEngine;
using UnityEngine.UI;

// A section header of a PlaceList: its title, and for a dropdown section the whole header as the button, how many
// places are in it, the chevron, and a hairline under it while it's closed.
public class PlaceListSection : MonoBehaviour
{
    public Button button;
    public Text title;
    public Text count;
    public Text chevron;
    public GameObject divider;
}
