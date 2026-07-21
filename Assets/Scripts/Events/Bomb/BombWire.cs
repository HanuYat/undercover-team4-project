using UnityEngine;

/// <summary>
/// 폭탄 선 1가닥 — E 클릭 한 번으로 자른다. (#232)
///
/// 상호작용은 소유 <see cref="BombDevice"/>로 넘겨 서버가 검증한다(이 컴포넌트는 서버 로직을 갖지 않는다 —
/// NpcSubdueInteractable가 PlayerEscorter로 위임하는 것과 같은 허브 패턴). 자기 색·인덱스만 노출하고,
/// 실제 색 렌더·절단 시각화는 뷰 계층이 <see cref="BombDevice.OnCutStateChanged"/> 등을 구독해 담당한다.
///
/// <see cref="BombDevice.Awake"/>가 자식 선을 모아 <see cref="Bind"/>로 인덱스를 매긴다 —
/// 그 인덱스가 퍼즐 색 배열·정답 판정의 기준이므로 프리팹 자식 순서가 곧 선 번호다.
/// </summary>
public class BombWire : MonoBehaviour, IInteractable
{
    private BombDevice m_device;
    private int m_index = -1;

    /// <summary>선 번호 — 프리팹 자식 순서. 퍼즐이 이 순서로 색·정답을 정한다.</summary>
    public int Index => m_index;

    /// <summary>이 선의 색 — 퍼즐에서 읽는다. 무장 전(퍼즐 없음)이면 기본값.</summary>
    public BombWireColor Color
    {
        get
        {
            if (m_device == null || m_device.Puzzle == null || m_index < 0)
                return BombWireColor.Red;
            return m_device.Puzzle.WireColors[m_index];
        }
    }

    /// <summary><see cref="BombDevice"/>가 Awake에서 바인딩한다.</summary>
    public void Bind(BombDevice device, int index)
    {
        m_device = device;
        m_index = index;
    }

    /// <summary>Interact가 거르는 조건과 동일 — 이미 잘렸거나 무장 상태가 아니면 윤곽선이 뜨지 않는다. (#184)</summary>
    public bool CanInteract(GameObject interactor) =>
        m_device != null && m_device.CanCutWire(m_index);

    public void Interact(GameObject interactor)
    {
        if (m_device == null)
            return;
        m_device.RequestCut(m_index, interactor);
    }
}
