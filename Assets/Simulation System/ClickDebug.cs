using UnityEngine;
using UnityEngine.EventSystems;

public class ClickDebug : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerClickHandler
{
    public void OnPointerDown(PointerEventData eventData) => Debug.Log($"DOWN on {gameObject.name}");
    public void OnPointerUp(PointerEventData eventData) =>
        Debug.Log($"UP on {gameObject.name} | eligibleForClick={eventData.eligibleForClick} | dragging={eventData.dragging} | currentRaycast={eventData.pointerCurrentRaycast.gameObject}");
    public void OnPointerClick(PointerEventData eventData) => Debug.Log($"CLICK on {gameObject.name}");
}