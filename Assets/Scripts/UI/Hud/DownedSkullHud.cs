using UnityEngine;

/// <summary>
/// 다운된 본인 화면의 해골 게이지 — App.UI.DownedSkull로 접근한다. (#493)
///
/// 표시 자체는 <see cref="IncapacitationSkullView"/>가 하고, 이 클래스는 <b>App 등록과 대상 연결</b>만
/// 맡는다. 월드 쪽 해골은 플레이어의 자식이라 부모에서 대상을 찾지만, HUD는 플레이어 밖에 있어
/// 누구를 보여줄지 밖에서 알려줘야 한다 — 오너의 <see cref="PlayerReviveHud"/>가 <see cref="Bind"/>한다.
///
/// 본인 화면에도 두는 이유: 다운되면 카메라가 바닥에 눕는데 자기 머리 위 아이콘은 올려다보지 않으면
/// 보이지 않는다. 남은 시간을 본인이 전혀 모르면 갑자기 기능 정지로 떨어진다. (#364와 같은 이유)
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class DownedSkullHud : CommonManagerBase
{
    [Tooltip("화면에 그릴 해골 게이지 — Face Camera는 꺼 둔다(화면 UI라 회전이 필요 없다)")]
    [SerializeField]
    private IncapacitationSkullView m_skull;

    /// <summary>누구의 상태를 보여줄지 지정한다. null이면 표시가 꺼진다.</summary>
    public void Bind(PlayerIncapacitation target)
    {
        if (m_skull != null)
            m_skull.Bind(target);
    }
}
