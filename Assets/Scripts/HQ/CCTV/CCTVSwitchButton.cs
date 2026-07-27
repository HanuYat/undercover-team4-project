using UnityEngine;

public enum ECctvSwitchDirection { Prev = -1, Next = 1 }

/// <summary>
/// CCTV 채널 전환 버튼 — 콘솔 화면과 분리된 설치물. (#362)
/// 순수 MonoBehaviour다: Interact()는 조준한 클라이언트에서 실행되므로(PlayerInteractor 오너 전용)
/// 여기서 스위처의 서버 RPC를 호출하면 되고, 버튼 자체엔 NetworkObject가 필요 없다.
/// </summary>
public class CCTVSwitchButton : MonoBehaviour, IInteractable
{
    [SerializeField] private CCTVSwitcher m_switcher;
    [SerializeField] private ECctvSwitchDirection m_direction = ECctvSwitchDirection.Next;

    public void Interact(GameObject interactor)
    {
        if (!CanInteract(interactor)) return;
        m_switcher.RequestSwitchRpc((int)m_direction);
    }

    public bool CanInteract(GameObject interactor) => 
        m_switcher != null 
        && m_switcher.IsSpawned 
        && m_switcher.ChannelCount > 1 
        && m_switcher.IsPowered;
}
