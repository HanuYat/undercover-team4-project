using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 쓰러진 동료 관련 온스크린 프롬프트 — 오너 화면 전용. (#105/#493, #725)
/// 다운(유예)·기능 정지(Die) 각각을 조준하면 일으키기/부활 키트 + 뒤지기 안내를, 내가 쓰러지면
/// "재부팅 중" 등 상태 문구를 띄운다(유예 잔여 초는 <see cref="DamageVignetteUI.ShowDownCountdown"/>이
/// 화면 중앙에 큰 숫자로 대신 그린다). 문구는 HUD의 공용 프롬프트(<see cref="PromptView"/>)에 얹는다.
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
        "동료가 나를 구조하는 중 — HudTable/Hud.Revive.BeingRevived. 유예 시계가 멈췄다는 신호"
    )]
    [SerializeField]
    private LocalizedString m_beingRevivedPrompt;

    [Tooltip("내가 기능 정지(Die) — HudTable/Hud.Revive.SelfDead")]
    [SerializeField]
    private LocalizedString m_selfDeadPrompt;

    [Header("행동 문구")]
    [Tooltip("다운된 아군을 조준 중 — HudTable/Hud.Revive.Hint. {0}=일으키기 키, {1}=뒤지기 키")]
    [SerializeField]
    private LocalizedString m_revivePrompt;

    [Tooltip(
        "기능 정지된 아군을 조준 중 — HudTable/Hud.Revive.DeadTarget. {0}=부활 키트 키, {1}=뒤지기 키"
    )]
    [SerializeField]
    private LocalizedString m_deadTargetPrompt;

    private PlayerReviver m_reviver;
    private PlayerIncapacitation m_incapacitation;
    private PlayerInputHandler m_input; // 문구에 끼울 키 표기 (#664)

    // 지금 띄워 둔 문구 — 매 프레임 같은 것을 다시 띄워 재구독하지 않도록 기억한다
    private LocalizedString m_shown;

    // 문구에 마지막으로 끼운 키 표기(들). null은 그 자리를 안 쓰는 문구. (#664)
    private string m_shownKey;
    private string m_shownKey2;

    // 스폰 시점의 오너를 굳힌다 — 사망 중에는 소유권이 서버로 넘어가므로(#763 A-1) IsOwner의 뜻이
    // 라운드 중에 뒤집힌다. 아래 Update는 스폰 때 정해진 enabled를 타므로 영향이 없지만,
    // <b>디스폰은 사망 중에도 일어난다</b>(라운드 리셋·퇴장) — 그때 IsOwner로 물으면 정리가 통째로
    // 건너뛰어져 "기능 정지" 문구와 유예 카운트다운이 화면에 그대로 눌어붙는다.
    private bool m_isLocalPlayer;

    public override void OnNetworkSpawn()
    {
        m_isLocalPlayer = IsOwner;

        if (!IsOwner)
        {
            enabled = false; // 남의 플레이어 것이 내 화면에 그려지지 않게 (오너 전용 HUD)
            return;
        }

        m_reviver = GetComponent<PlayerReviver>();
        m_incapacitation = GetComponent<PlayerIncapacitation>();
        m_input = GetComponent<PlayerInputHandler>();
    }

    public override void OnNetworkDespawn()
    {
        if (m_isLocalPlayer)
        {
            ClearPrompt(); // 퇴장·씬 전환으로 사라질 때 문구가 화면에 남지 않게
            App.UI.DamageVignette?.HideDownCountdown();
        }
    }

    private void Update() // 비오너는 OnNetworkSpawn에서 비활성화되므로 오너만 돈다
    {
        // 라운드 정산 화면이 떠 있으면 그 위로 겹쳐 그리지 않는다.
        // (전원 다운으로 라운드가 끝나면 나는 여전히 무력화 상태라 "다운됨"이 정산 위로 샌다)
        if (
            App.UI.Current != null
            && App.UI.Current.TryGetPanel(out SettlementPanel settlement)
            && settlement.IsOpened
        )
        {
            ClearPrompt();
            App.UI.DamageVignette?.HideDownCountdown();
            return;
        }

        bool isDowned = m_incapacitation != null && m_incapacitation.IsDowned;
        if (!isDowned)
            App.UI.DamageVignette?.HideDownCountdown();

        // 다운 — 남은 초는 화면 중앙에 큰 숫자로, 구조 채널링 중이면 "재부팅 중" 문구를 더한다. (#725)
        if (isDowned)
        {
            App.UI.DamageVignette?.ShowDownCountdown(
                Mathf.CeilToInt(m_incapacitation.RemainingUntilDie)
            );

            if (m_incapacitation.IsBeingRevived)
                SetPrompt(m_beingRevivedPrompt);
            else
                ClearPrompt();
            return;
        }

        // 내가 기능 정지(Die)된 경우 — 동료의 부활 키트를 기다려야 한다는 안내 (#364/#613)
        // 몸이 회수 불가능한 곳으로 사라졌으면(맨홀 납치, #775) 부활이 영영 없으므로 이 안내 자체가
        // 거짓이다 — 아무 문구도 띄우지 않는다.
        if (m_incapacitation != null && m_incapacitation.IsDead)
        {
            if (m_incapacitation.IsRevivable)
                SetPrompt(m_selfDeadPrompt);
            else
                ClearPrompt();
            return;
        }

        // 무력화 중에는 조준 안내를 적지 않는다 (#664). 위 두 분기가 걸러 낸 다운·기능 정지 말고도
        // 기절·페널티·납치가 여기로 내려오는데, 그동안은 좌클릭도 E도 가드에 막혀 아무것도 못 한다.
        // 조준 안내(InteractionFeedback)가 같은 상황에서 사라지므로 기준을 맞춘다.
        if (m_incapacitation != null && m_incapacitation.IsIncapacitated)
        {
            ClearPrompt();
            return;
        }

        // 여기부터는 조준 중에 뜨는 안내다 — 조준 안내(#664)가 이미 떠 있으면 띄우지 않는다.
        // 같은 화면에 두 줄이 뜨고, 심하면 서로 다른 말을 한다(밧줄을 들고 기능 정지된 동료를
        // 겨누면 실제로 먹히는 것은 '업기'인데 이쪽은 '부활 키트를 들고'를 계속 적는다).
        if (App.UI.InteractPrompt != null && App.UI.InteractPrompt.IsPromptShowing)
        {
            ClearPrompt();
            return;
        }

        // 다운된 아군을 조준 중 — 일으키기(E) + 뒤지기(R) 한 줄로 함께 안내한다. 유예 중인 몸은
        // 둘 다 되므로(#725) 한 줄에 합쳐 읽는 부담을 줄인다(팀 결정).
        if (m_reviver != null && m_reviver.CurrentReviveTarget != null)
        {
            SetPrompt(m_revivePrompt, InteractKey, LootKey);
            return;
        }

        // 기능 정지된 아군을 조준 중 — 부활 키트(좌클릭) + 뒤지기(R) 한 줄. (#364/#613/#725)
        if (m_reviver != null && m_reviver.CurrentDeadTarget != null)
        {
            SetPrompt(m_deadTargetPrompt, UseItemKey, LootKey);
            return;
        }

        ClearPrompt();
    }

    // 키 표기 — 입력 처리기가 없는 구성(단독 테스트 등)이면 문구에 빈칸이 남지 않게 물음표를 적는다.
    private string InteractKey => m_input != null ? m_input.InteractBinding : "?";

    private string UseItemKey => m_input != null ? m_input.UseItemBinding : "?";

    private string LootKey => m_input != null ? m_input.LootBinding : "?";

    /// <summary>
    /// keyLabel(들)이 있으면 문구의 인자({0}, {1})에 끼운다. (#664, #725)
    /// </summary>
    private void SetPrompt(LocalizedString prompt, string keyLabel = null, string keyLabel2 = null)
    {
        if (prompt == null || prompt.IsEmpty)
            return;

        if (ReferenceEquals(m_shown, prompt))
        {
            // 같은 문구다 — 인자만 바뀌었으면(재바인딩·남은 초 갱신) 재구독 없이 다시 포맷만 한다.
            // 매 프레임 Show를 다시 부르면 초당 수십 번 구독을 갈아치운다.
            if (keyLabel != m_shownKey || keyLabel2 != m_shownKey2)
            {
                m_shownKey = keyLabel;
                m_shownKey2 = keyLabel2;
                ApplyArguments(prompt, keyLabel, keyLabel2);
                prompt.RefreshString(); // 이미 구독 중이므로 다시 포맷만 시킨다
            }
            return;
        }

        // 인자를 먼저 넣어야 구독 시점의 첫 발화부터 올바른 문장이 나온다
        ApplyArguments(prompt, keyLabel, keyLabel2);

        m_shown = prompt;
        m_shownKey = keyLabel;
        m_shownKey2 = keyLabel2;
        App.UI.Prompt?.Show(prompt);
    }

    private static void ApplyArguments(LocalizedString prompt, string keyLabel, string keyLabel2)
    {
        if (keyLabel == null)
            return;

        prompt.Arguments =
            keyLabel2 == null ? new object[] { keyLabel } : new object[] { keyLabel, keyLabel2 };
    }

    private void ClearPrompt()
    {
        if (m_shown == null)
            return;

        // 내가 띄운 것만 지운다 — 그 사이 다른 곳이 덮어썼으면 건드리지 않는다
        App.UI.Prompt?.Hide(m_shown);
        m_shown = null;
        m_shownKey = null;
        m_shownKey2 = null;
    }
}
