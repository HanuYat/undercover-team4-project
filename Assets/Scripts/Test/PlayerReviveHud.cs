using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 구조 관련 온스크린 프롬프트 — 오너 화면 전용. (#105/#493)
///
/// <b>역할이 둘로 갈린다</b>: 상태(다운·기능 정지)는 채워지는 해골이 보여주고
/// (<see cref="IncapacitationSkullView"/>), 이 클래스는 <b>"내가 지금 뭘 할 수 있나"</b>만 문구로 띄운다
/// — 운반 중 안내, 구조 키 안내, 구조 불가 안내. 상태를 문구로 중복 설명하지 않는다.
///
/// 본인 화면의 해골(<see cref="DownedSkullHud"/>)에 자기 무력화 상태를 연결하는 것도 여기서 한다.
/// HUD는 씬마다 다시 만들어지므로(InteractionFeedback.EnsureHud) 참조가 바뀌면 다시 연결한다.
/// </summary>
[RequireComponent(typeof(PlayerReviver))]
public class PlayerReviveHud : NetworkBehaviour
{
    [Header("프롬프트 문구")]
    [Tooltip("동료를 운반 중 — UITable/revive.carrying")]
    [SerializeField]
    private LocalizedString m_carryingPrompt;

    [Tooltip("다운된 아군을 조준 중 — UITable/revive.hint")]
    [SerializeField]
    private LocalizedString m_revivePrompt;

    [Tooltip("기능 정지된 아군을 조준 중 — UITable/revive.dead_target")]
    [SerializeField]
    private LocalizedString m_deadTargetPrompt;

    private PlayerReviver m_reviver;
    private PlayerIncapacitation m_incapacitation;
    private PlayerCarrier m_carrier; // 운반 프롬프트 (#365)

    // 지금 띄워 둔 문구 — 매 프레임 같은 것을 다시 띄워 재구독하지 않도록 기억한다
    private LocalizedString m_shown;

    // 마지막으로 연결한 HUD 해골 — 씬 전환으로 HUD가 새로 생기면 다시 연결한다
    private DownedSkullHud m_boundSkull;

    public override void OnNetworkSpawn()
    {
        if (!IsOwner)
        {
            enabled = false; // 남의 플레이어 것이 내 화면에 그려지지 않게 (오너 전용 HUD)
            return;
        }

        m_reviver = GetComponent<PlayerReviver>();
        m_incapacitation = GetComponent<PlayerIncapacitation>();
        m_carrier = GetComponent<PlayerCarrier>();
    }

    public override void OnNetworkDespawn()
    {
        if (!IsOwner)
            return;

        // 퇴장·씬 전환으로 사라질 때 화면에 문구와 해골이 남지 않게 한다
        ClearPrompt();
        m_boundSkull?.Bind(null);
        m_boundSkull = null;
    }

    private void Update() // 비오너는 OnNetworkSpawn에서 비활성화되므로 오너만 돈다
    {
        BindSkull();
        RefreshPrompt();
    }

    // 본인 화면 해골에 내 상태를 연결한다 — HUD는 씬마다 새로 만들어지므로 참조가 바뀔 때마다 다시 한다
    private void BindSkull()
    {
        DownedSkullHud skull = App.UI.DownedSkull;
        if (skull == m_boundSkull)
            return;

        m_boundSkull = skull;
        skull?.Bind(m_incapacitation);
    }

    private void RefreshPrompt()
    {
        // 라운드 정산 화면이 떠 있으면 그 위로 겹쳐 그리지 않는다.
        // (전원 다운으로 라운드가 끝나면 나는 여전히 무력화 상태라 프롬프트가 정산 위로 샌다)
        if (App.UI.Current != null
            && App.UI.Current.TryGetPanel(out SettlementPanel settlement)
            && settlement.IsOpened)
        {
            ClearPrompt();
            return;
        }

        // 내가 쓰러져 있는 동안엔 할 수 있는 행동이 없다 — 상태는 해골이 보여주므로 문구는 비운다.
        // IsIncapacitated가 아니라 IsOutOfAction을 본다 (#252): 기절·오검거 매달기는 스스로 풀리고
        // 그 사이에도 조준·운반 프롬프트가 의미 있을 수 있다.
        if (m_incapacitation != null && m_incapacitation.IsOutOfAction)
        {
            ClearPrompt();
            return;
        }

        // 동료를 운반 중 — 목적지와 내려놓기 안내 (#365)
        if (m_carrier != null && m_carrier.IsCarrying)
        {
            SetPrompt(m_carryingPrompt);
            return;
        }

        // 다운된 아군을 조준 중이면 구조 키 프롬프트
        if (m_reviver != null && m_reviver.CurrentReviveTarget != null)
        {
            SetPrompt(m_revivePrompt);
            return;
        }

        // 기능 정지된 아군을 조준 중 — 구조가 아니라 운반이 답이다 (#364/#365)
        if (m_reviver != null && m_reviver.CurrentDeadTarget != null)
        {
            SetPrompt(m_deadTargetPrompt);
            return;
        }

        ClearPrompt();
    }

    private void SetPrompt(LocalizedString prompt)
    {
        if (prompt == null || prompt.IsEmpty)
            return;

        // 같은 문구면 다시 띄우지 않는다 — 매 프레임 재구독을 피한다
        if (ReferenceEquals(m_shown, prompt))
            return;

        m_shown = prompt;
        App.UI.Prompt?.Show(prompt);
    }

    private void ClearPrompt()
    {
        if (m_shown == null)
            return;

        // 내가 띄운 것만 지운다 — 그 사이 다른 곳이 덮어썼으면 건드리지 않는다
        App.UI.Prompt?.Hide(m_shown);
        m_shown = null;
    }
}
