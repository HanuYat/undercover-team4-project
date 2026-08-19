using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Tab 홀드로 팀 상황판을 여닫는다 (#720) — 오너 로컬 전용이다.
/// <see cref="PlayerEmoteInput"/>과 같은 자리에 있는 컴포넌트다: 입력은 플레이어가 받고 창은 UI가 그린다.
///
/// 무력화 중에도 막지 않는다 — 표시만 하는 창이라 쓰러진 사람이 팀 상태를 보는 것이 오히려 자연스럽다.
/// </summary>
[RequireComponent(typeof(PlayerInputHandler))]
public class PlayerTeamStatusInput : NetworkBehaviour
{
    private PlayerInputHandler m_inputHandler;

    public override void OnNetworkSpawn()
    {
        if (!IsOwner)
        {
            enabled = false;
            return;
        }

        m_inputHandler = GetComponent<PlayerInputHandler>();
        m_inputHandler.OnTeamStatusOpened += Open;
        m_inputHandler.OnTeamStatusClosed += Close;
    }

    public override void OnNetworkDespawn()
    {
        if (!IsOwner || m_inputHandler == null)
            return;

        m_inputHandler.OnTeamStatusOpened -= Open;
        m_inputHandler.OnTeamStatusClosed -= Close;

        Close(); // 씬 전환·퇴장으로 사라져도 창이 화면에 남지 않게
    }

    // 창이 떠 있는 동안 컴포넌트가 꺼져도 패널이 화면에 굳지 않게 한다.
    private void OnDisable() => Close();

    /// <summary>
    /// 포커스가 빠지면 닫는다 — 하필 키가 Tab이라 <b>Alt+Tab</b>으로 나가는 경우가 잦다.
    /// 그때 canceled가 돌아오지 않으면 떠나간 뒤에도 창이 열린 채 남는다. (#720)
    /// </summary>
    private void OnApplicationFocus(bool hasFocus)
    {
        if (!hasFocus)
            Close();
    }

    // 매니저 경유로 열고 닫는다 (PanelBase 관례) — 패널이 등록되지 않았으면
    // OpenPanel이 콘솔에 남긴다(#441). 직접 찾아 열면 배선이 빠졌을 때 Tab이 조용히 죽는다.
    private void Open()
    {
        if (App.UI.Current != null)
            App.UI.Current.OpenPanel<TeamStatusPanel>();

        // 보는 동안은 감정표현 휠·인벤토리 편집을 막는다 — 둘 다 상황판 밑에 깔려 안 보인다.
        m_inputHandler.SetTeamStatusPeeking(true);
    }

    private void Close()
    {
        // 오너일 때만 설정되는 참조라 이걸로 오너를 가른다 — 남의 플레이어 오브젝트도
        // 스폰 때 enabled=false로 꺼지며 OnDisable을 지나가는데, 그게 내 창을 닫으면 안 된다.
        if (m_inputHandler == null)
            return;

        if (App.UI.Current != null)
            App.UI.Current.ClosePanel<TeamStatusPanel>();

        m_inputHandler.SetTeamStatusPeeking(false);
    }
}
