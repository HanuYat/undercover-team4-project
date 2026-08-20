using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 다운 유예 화면 어두워짐 — 오너 전용. (#725)
/// 어두워짐 = 1 - RemainingUntilDie / DieAfterDownSeconds. 구조 채널링 중엔 RemainingUntilDie가
/// 얼어붙으므로 화면도 저절로 멈춘다.
///
/// Down → Die 전이 순간은 예외다: 완전 암전을 <see cref="m_deathHoldSeconds"/>만큼 유지한 뒤
/// <see cref="m_deathFadeSeconds"/>에 걸쳐 페이드하며 관전 카메라를 드러낸다. 같은 구간 동안
/// SFX/BGM(AudioListener.volume)과 Vivox 출력(<see cref="VivoxManager.ForceMuteOutput"/>)도 끈다 —
/// Vivox는 AudioListener를 안 거치는 별개 경로라 따로 처리한다.
///
/// PTT 송신은 홀드·페이드와 무관하게 Die인 내내 막고(<see cref="VivoxManager.SetTransmitBlocked"/>),
/// 부활 즉시 푼다. Down 유예 중에는 계속 말할 수 있다.
/// </summary>
[RequireComponent(typeof(PlayerIncapacitation))]
public class PlayerDownView : NetworkBehaviour
{
    [Tooltip("완전 사망 순간 완전 암전(과 무음)을 그대로 유지하는 시간(초)")]
    [SerializeField]
    private float m_deathHoldSeconds = 1f;

    [Tooltip("암전 유지가 끝난 뒤 관전 카메라가 드러나기까지 페이드 시간(초)")]
    [SerializeField]
    private float m_deathFadeSeconds = 0.6f;

    private PlayerIncapacitation m_incapacitation;

    // 스폰 전(오프라인)에는 IsOwner가 늘 false다 — 그때는 자기 화면이 곧 내 화면이므로 오너로 본다.
    // (PlayerHitView.IsLocalOwner와 같은 형태)
    //
    // ⚠ <b>스폰 시점의 오너를 굳혀서 쓴다.</b> 사망 중에는 소유권이 서버로 넘어가므로(#763 A-1)
    // IsOwner를 매번 물으면 이 화면 연출이 <b>양쪽에서 동시에 틀린다</b>:
    //  · 죽는 본인 — 소유권을 잃어 Update가 먼저 빠져나가고, 암전·무음·PTT 차단이 통째로 안 걸린다
    //  · 호스트 — 남의 시체가 "내 몸"이 되어, 남이 죽었는데 내 화면이 어두워지고 내 송신이 막힌다
    //
    // 이 연출이 물어야 하는 것은 "지금 이 몸의 주인인가"가 아니라 <b>"내가 죽었는가"</b>다.
    private bool IsLocalOwner => !IsSpawned || m_isLocalPlayer;

    private bool m_isLocalPlayer;

    // 라운드 중에 뒤집히는 값이라 스폰 시점에 굳힌다 (#763 A-1 — 위 IsLocalOwner 주석).
    public override void OnNetworkSpawn() => m_isLocalPlayer = IsOwner;

    private IncapacitationCause m_lastCause = IncapacitationCause.None;

    // 음수 = 진행 중 아님. 0 이상이면 Die 전이 이후 경과 시간 — 홀드·페이드를 이 값 하나로 판정한다.
    private float m_deathElapsed = -1f;

    // AudioListener를 눌러 둔 상태인가 — 복원을 정확히 한 번만 하기 위한 가드.
    private bool m_isMuted;

    private void Awake()
    {
        m_incapacitation = GetComponent<PlayerIncapacitation>();
    }

    public override void OnNetworkDespawn()
    {
        if (IsLocalOwner)
            App.UI.DamageVignette?.SetDownDarkness(0f);

        RestoreVolume();

        // 연결 끊김 등으로 Die 중 디스폰되면 새 뷰가 None에서 시작해 아래 토글을 다시 못 타므로
        // 여기서 확실히 푼다.
        if (m_lastCause == IncapacitationCause.Die)
            App.Net.Vivox?.SetTransmitBlocked(false);

        m_lastCause = IncapacitationCause.None;
        m_deathElapsed = -1f;

        base.OnNetworkDespawn();
    }

    private void Update()
    {
        if (!IsLocalOwner)
            return;

        if (m_incapacitation == null)
        {
            App.UI.DamageVignette?.SetDownDarkness(0f);
            return;
        }

        IncapacitationCause cause = m_incapacitation.Cause;

        if (cause == IncapacitationCause.Die && m_lastCause != IncapacitationCause.Die)
            App.Net.Vivox?.SetTransmitBlocked(true);
        else if (cause != IncapacitationCause.Die && m_lastCause == IncapacitationCause.Die)
            App.Net.Vivox?.SetTransmitBlocked(false);

        if (m_lastCause == IncapacitationCause.Down && cause == IncapacitationCause.Die)
        {
            m_deathElapsed = 0f;
            AudioListener.volume = 0f;
            App.Net.Vivox?.ForceMuteOutput();
            m_isMuted = true;
        }
        m_lastCause = cause;

        // 홀드·페이드 도중 부활 키트로 즉시 살아나면 시퀀스를 끊는다 — 안 그러면 이미 움직일 수
        // 있는 캐릭터의 화면이 남은 시간만큼 계속 암전으로 남는다.
        if (m_deathElapsed >= 0f && cause != IncapacitationCause.Die)
        {
            m_deathElapsed = -1f;
            RestoreVolume();
        }

        if (m_deathElapsed >= 0f)
        {
            m_deathElapsed += Time.deltaTime;

            if (m_deathElapsed < m_deathHoldSeconds)
            {
                App.UI.DamageVignette?.SetDownDarkness(1f);
                return;
            }

            RestoreVolume();

            float fadeElapsed = m_deathElapsed - m_deathHoldSeconds;
            float fadeRatio =
                m_deathFadeSeconds > 0f ? Mathf.Clamp01(fadeElapsed / m_deathFadeSeconds) : 1f;
            App.UI.DamageVignette?.SetDownDarkness(1f - fadeRatio);

            if (fadeRatio >= 1f)
                m_deathElapsed = -1f;

            return;
        }

        if (cause != IncapacitationCause.Down)
        {
            App.UI.DamageVignette?.SetDownDarkness(0f);
            return;
        }

        float total = m_incapacitation.DieAfterDownSeconds;
        float darkness = total > 0f ? 1f - m_incapacitation.RemainingUntilDie / total : 1f;
        App.UI.DamageVignette?.SetDownDarkness(darkness);
    }

    // 하드코딩한 1이 아니라 GameSettings.MasterVolume을 쓰는 이유는 음량 슬라이더를 지키기 위해서다.
    private void RestoreVolume()
    {
        if (!m_isMuted)
            return;

        AudioListener.volume = GameSettings.MasterVolume;
        App.Net.Vivox?.ApplyVoiceVolume();
        m_isMuted = false;
    }
}
