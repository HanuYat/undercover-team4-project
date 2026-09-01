using UnityEngine;

// 휴대용 미니맵 위젯 표시 (#835). 장착 여부 판단은 PortableMinimapWatcher(NetworkBehaviour,
// Player 프리팹)가 하고 여긴 결과만 받아 위젯을 켜고 끈다 — HUD는 오너 클라에서만 Instantiate되는
// 로컬 오브젝트라 NetworkBehaviour를 못 올린다. ChannelingGaugeUI와 같은 구조.
public class PortableMinimapHud : CommonManagerBase
{
    [SerializeField]
    private GameObject m_widgetRoot;

    protected override void Awake()
    {
        base.Awake();
        SetVisible(false);
    }

    public void SetVisible(bool visible)
    {
        if (m_widgetRoot != null)
            m_widgetRoot.SetActive(visible);
    }
}
