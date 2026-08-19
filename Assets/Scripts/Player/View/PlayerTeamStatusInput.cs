using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Tab 홀드로 팀 상황판을 여닫는다 (#720) — 오너 로컬 전용. 입력은 플레이어가 받고 창은 UI가 그린다.
/// 무력화 중에도 막지 않는다 — 쓰러진 사람이 팀 상태를 보는 것이 오히려 자연스럽다.
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

    // 포커스가 빠지면 닫는다 — 키가 Tab이라 Alt+Tab이 잦고, 그때 canceled가 안 오면 창이 열린 채 굳는다.
    private void OnApplicationFocus(bool hasFocus)
    {
        if (!hasFocus)
            Close();
    }

    // 매니저 경유로 연다 (PanelBase 관례) — 배선이 빠지면 OpenPanel이 콘솔에 남긴다(#441).
    private void Open()
    {
        if (App.UI.Current != null)
            App.UI.Current.OpenPanel<TeamStatusPanel>();

        // 보는 동안은 감정표현 휠·인벤토리 편집을 막는다 — 둘 다 상황판 밑에 깔려 안 보인다.
        m_inputHandler.SetTeamStatusPeeking(true);
    }

    private void Close()
    {
        // 오너일 때만 설정되는 참조로 오너를 가른다 — 남의 오브젝트도 스폰 때 꺼지며 OnDisable을 지나간다.
        if (m_inputHandler == null)
            return;

        if (App.UI.Current != null)
            App.UI.Current.ClosePanel<TeamStatusPanel>();

        m_inputHandler.SetTeamStatusPeeking(false);
    }
}
