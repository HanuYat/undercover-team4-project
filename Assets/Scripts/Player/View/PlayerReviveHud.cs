using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 쓰러진 동료 관련 온스크린 프롬프트 — 오너 화면 전용. (#105/#493)
/// 기능 정지된 아군을 조준하면 운반 안내를, 내가 기능 정지되면 본부 이송 대기 메시지를 띄운다.
/// 현장 구조(다운) 쪽 분기는 #524로 휴면 상태지만, 되살릴 때 그대로 쓰도록 남겨 뒀다.
/// 문구는 HUD의 공용 프롬프트(<see cref="PromptView"/>)에 얹는다 — 이 클래스는 상태를 보고
/// 어떤 문구를 띄울지만 고른다.
///
/// <b>월드 아이콘(채워지는 해골)을 시도했다가 되돌렸다</b>: 평면 스프라이트라 옆에서 보면 보이지
/// 않고, 상태를 알리는 수단으로 텍스트보다 나을 게 없었다. 시각화로 다시 갈 거면 평면 이미지가
/// 아닌 방식(캐릭터 자체의 연출 등)이어야 한다.
/// </summary>
[RequireComponent(typeof(PlayerReviver))]
public class PlayerReviveHud : NetworkBehaviour
{
    [Header("상태 문구")]
    [Tooltip(
        "내가 다운됨 — HudTable/Hud.Revive.Downed. #524로 다운이 발생하지 않아 현재는 뜨지 않는다"
    )]
    [SerializeField]
    private LocalizedString m_downedPrompt;

    [Tooltip("내가 기능 정지(Die) — HudTable/Hud.Revive.SelfDead")]
    [SerializeField]
    private LocalizedString m_selfDeadPrompt;

    [Header("행동 문구")]
    [Tooltip("동료를 운반 중 — HudTable/Hud.Revive.Carrying")]
    [SerializeField]
    private LocalizedString m_carryingPrompt;

    [Tooltip("다운된 아군을 조준 중 — HudTable/Hud.Revive.Hint")]
    [SerializeField]
    private LocalizedString m_revivePrompt;

    [Tooltip("기능 정지된 아군을 조준 중 — HudTable/Hud.Revive.DeadTarget")]
    [SerializeField]
    private LocalizedString m_deadTargetPrompt;

    private PlayerReviver m_reviver;
    private PlayerIncapacitation m_incapacitation;
    private PlayerCarrier m_carrier; // 운반 프롬프트 (#365)

    // 지금 띄워 둔 문구 — 매 프레임 같은 것을 다시 띄워 재구독하지 않도록 기억한다
    private LocalizedString m_shown;

    // 카운트다운 문구에 마지막으로 넣은 남은 초. -1은 인자 없는 문구.
    private int m_shownSeconds = -1;

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
        if (IsOwner)
            ClearPrompt(); // 퇴장·씬 전환으로 사라질 때 문구가 화면에 남지 않게
    }

    private void Update() // 비오너는 OnNetworkSpawn에서 비활성화되므로 오너만 돈다
    {
        // 라운드 정산 화면이 떠 있으면 그 위로 겹쳐 그리지 않는다.
        // (전원 다운으로 라운드가 끝나면 나는 여전히 무력화 상태라 "다운됨"이 정산 위로 샌다)
        if (App.UI.Current != null
            && App.UI.Current.TryGetPanel(out SettlementPanel settlement)
            && settlement.IsOpened)
        {
            ClearPrompt();
            return;
        }

        // 내가 다운된 경우 — 구조 대기 메시지.
        // IsIncapacitated가 아니라 IsDowned를 본다 (#252): 기절·오검거 매달기도 무력화지만 스스로
        // 풀리므로 구조를 기다리라는 안내가 거짓이 된다. 아무도 오지 않는데 기다리게 만든다.
        // #524 이후 Down은 발생하지 않아 이 분기는 휴면 상태다 — 현장 구조를 되살릴 때 같이 깨어난다.
        // (Die까지 남은 시간을 함께 보여주던 카운트다운 인자는 제한시간 자체가 사라져 빠졌다)
        if (m_incapacitation != null && m_incapacitation.IsDowned)
        {
            SetPrompt(m_downedPrompt);
            return;
        }

        // 내가 기능 정지(Die)된 경우 — 본부 이송(#365)만 남았다는 안내 (#364)
        if (m_incapacitation != null && m_incapacitation.IsDead)
        {
            SetPrompt(m_selfDeadPrompt);
            return;
        }

        // 동료를 운반 중 — 목적지와 내려놓기 안내 (#365)
        if (m_carrier != null && m_carrier.IsCarrying)
        {
            SetPrompt(m_carryingPrompt);
            return;
        }

        // 다운된 아군을 조준 중이면 구조 키 프롬프트 (#524로 휴면 — 위 IsDowned 분기와 같은 이유)
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

    /// <summary>seconds >= 0이면 Smart String 인자({0})로 남은 초를 끼운다.</summary>
    private void SetPrompt(LocalizedString prompt, int seconds = -1)
    {
        if (prompt == null || prompt.IsEmpty)
            return;

        if (ReferenceEquals(m_shown, prompt))
        {
            // 같은 문구다 — 남은 초만 바뀌었으면 재구독 없이 인자만 갱신한다.
            // 매 프레임 Show를 다시 부르면 초당 수십 번 구독을 갈아치운다.
            if (seconds != m_shownSeconds)
            {
                m_shownSeconds = seconds;
                ApplyArguments(prompt, seconds);
                prompt.RefreshString(); // 이미 구독 중이므로 다시 포맷만 시킨다
            }
            return;
        }

        // 인자를 먼저 넣어야 구독 시점의 첫 발화부터 올바른 문장이 나온다
        ApplyArguments(prompt, seconds);

        m_shown = prompt;
        m_shownSeconds = seconds;
        App.UI.Prompt?.Show(prompt);
    }

    private static void ApplyArguments(LocalizedString prompt, int seconds)
    {
        if (seconds >= 0)
            prompt.Arguments = new object[] { seconds };
    }

    private void ClearPrompt()
    {
        if (m_shown == null)
            return;

        // 내가 띄운 것만 지운다 — 그 사이 다른 곳이 덮어썼으면 건드리지 않는다
        App.UI.Prompt?.Hide(m_shown);
        m_shown = null;
        m_shownSeconds = -1;
    }
}
