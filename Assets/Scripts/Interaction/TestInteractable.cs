using UnityEngine;

public class TestInteractable : MonoBehaviour, IInteractable
{
    public void Interact(GameObject interactor)
    {
        Debug.Log($"{name} 상호작용됨 (by {interactor.name})");

        var renderer = GetComponent<Renderer>();
        if (renderer != null)
        {
            renderer.material.color = Random.ColorHSV(); // 눈으로도 확인
        }
    }
}
