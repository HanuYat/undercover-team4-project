using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 다운 유예 화면 어두워짐 — 오너 전용. (#725)
/// 다운 중엔 어두워짐 = 1 - RemainingUntilDie / DieAfterDownSeconds 를 그대로 반영한다. 구조 채널링
/// 중에는 RemainingUntilDie가 얼어붙으므로(<see cref="PlayerIncapacitation"/>) 화면도 저절로 멈춘다 —
/// 여기서 따로 보간·타이머를 두지 않는다(PlayerHitView 관례와 달리 순간 이벤트가 아니라 매 프레임
/// 값을 그대로 반영하는 상태 표시라서다).
///
/// Down → Die 전이 순간만 예외다: 완전 암전(1)을 <see cref="m_deathHoldSeconds"/>만큼 그대로 유지한 뒤
/// <see cref="m_deathFadeSeconds"/>에 걸쳐 0으로 페이드하며 관전 카메라(PlayerLook.SetSpectateView)를
/// 드러낸다. IsDowned가 꺼지는 걸 그대로 밝기 0으로 읽으면 완전 사망이 오히려 화면을 밝히는 것으로 보인다.
///
/// 같은 홀드 구간 동안 소리도 끈다 — 화면 암전과 짝을 맞춰 "잠깐 정신을 잃는" 느낌을 준다.
/// <see cref="SoundManager"/>는 "전역 음량은 건드리지 않는다"는 규칙을 지키므로(#225 — GameSettings가
/// AudioListener.volume을 이미 쓴다), 여기서 직접 잠깐 0으로 내렸다가 설정값으로 복원한다. 화면 암전과
/// 마찬가지로 오너의 클라이언트에만 있는 하나뿐인 AudioListener를 건드리는 것이라 남에게는 들리지 않는다.
///
/// Vivox 무전·근접 음성은 AudioListener를 거치지 않는 별개 경로라(GameSettings.MasterVolume 문서 참고)
/// 따로 <see cref="VivoxManager.ForceMuteOutput"/>/<see cref="VivoxManager.ApplyVoiceVolume"/>으로
/// 같은 구간에 맞춰 껐다 되살린다.
///
/// <b>말하기(송신)는 별개 축이다</b> — 완전 사망(Die) 동안은 통째로 PTT가 막힌다(홀드·페이드와
/// 무관하게 Die인 내내). 부활 키트로 되살아나는 즉시 <see cref="VivoxManager.SetTransmitBlocked"/>로
/// 풀린다. 다운(Down) 유예 중에는 여전히 말할 수 있다 — 막는 건 완전 사망뿐이다.
/// </summary>
[RequireComponent(typeof(PlayerIncapacitation))]
public class PlayerDownView : NetworkBehaviour
{
    [Tooltip("완전 사망 순간 완전 암전(과 무음)을 그대로 유지하는 시간(초) — 이 동안은 밝아지지도, 소리가 나지도 않는다")]
    [SerializeField]
    private float m_deathHoldSeconds = 1f;

    [Tooltip("암전 유지가 끝난 뒤 관전 카메라가 드러나기까지 페이드 시간(초)")]
    [SerializeField]
    private float m_deathFadeSeconds = 0.6f;

    private PlayerIncapacitation m_incapacitation;

    // 스폰 전(오프라인)에는 IsOwner가 늘 false다 — 그때는 자기 화면이 곧 내 화면이므로 오너로 본다.
    // (PlayerHitView.IsLocalOwner와 동일 관례)
    private bool IsLocalOwner => !IsSpawned || IsOwner;

    // Down → Die 전이 감지용 — 매 프레임 Cause를 직접 비교한다(IsDowned bool만 보면 전이 방향을 모른다).
    private IncapacitationCause m_lastCause = IncapacitationCause.None;

    // 음수 = 진행 중 아님 (DamageVignetteUI.m_vignetteElapsed 관례). 0 이상이면 Die 전이 이후 경과 시간 —
    // 홀드·페이드 두 구간을 이 값 하나로 함께 판정한다(아래 Update).
    private float m_deathElapsed = -1f;

    // 지금 이 컴포넌트가 AudioListener를 눌러 둔 상태인가 — 복원을 정확히 한 번만 하기 위한 가드.
    private bool m_isMuted;

    private void Awake()
    {
        m_incapacitation = GetComponent<PlayerIncapacitation>();
    }

    public override void OnNetworkDespawn()
    {
        // 씬 전환·리스폰으로 뷰가 사라질 때 화면이 어두운 채로·소리가 죽은 채로 남지 않게 (PlayerHitView 관례)
        if (IsLocalOwner)
            App.UI.DamageVignette?.SetDownDarkness(0f);

        RestoreVolume();

        // 완전 사망 중에 디스폰되는 드문 경로(연결 끊김 등)에서도 송신 차단이 눌어붙지 않게 한다 —
        // 새로 스폰될 뷰는 None에서 시작해 이 else 분기를 다시 타지 않으므로, 여기서 확실히 풀어야 한다.
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

        // 완전 사망 진입/이탈 — 홀드·페이드와 무관하게 Die인 내내 말을 막는다.
        if (cause == IncapacitationCause.Die && m_lastCause != IncapacitationCause.Die)
            App.Net.Vivox?.SetTransmitBlocked(true);
        else if (cause != IncapacitationCause.Die && m_lastCause == IncapacitationCause.Die)
            App.Net.Vivox?.SetTransmitBlocked(false); // 부활 즉시 다시 말할 수 있게

        if (m_lastCause == IncapacitationCause.Down && cause == IncapacitationCause.Die)
        {
            m_deathElapsed = 0f; // 지금 이 프레임에 완전 사망으로 넘어갔다 — 암전·무음 유지 시작
            AudioListener.volume = 0f;
            App.Net.Vivox?.ForceMuteOutput();
            m_isMuted = true;
        }
        m_lastCause = cause;

        if (m_deathElapsed >= 0f)
        {
            m_deathElapsed += Time.deltaTime;

            if (m_deathElapsed < m_deathHoldSeconds)
            {
                App.UI.DamageVignette?.SetDownDarkness(1f); // 홀드 구간 — 완전 암전·무음 그대로
                return;
            }

            RestoreVolume(); // 홀드가 끝나는 첫 프레임에 한 번만 — 이후 프레임은 m_isMuted 가드로 무동작

            float fadeElapsed = m_deathElapsed - m_deathHoldSeconds;
            float fadeRatio =
                m_deathFadeSeconds > 0f ? Mathf.Clamp01(fadeElapsed / m_deathFadeSeconds) : 1f;
            App.UI.DamageVignette?.SetDownDarkness(1f - fadeRatio);

            if (fadeRatio >= 1f)
                m_deathElapsed = -1f; // 페이드 끝 — 이후엔 아래 일반 분기로

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

    // 죽음 홀드가 눌러 둔 볼륨을 설정값으로 복원한다 — 하드코딩한 1이 아니라 GameSettings.MasterVolume·
    // VivoxManager.ApplyVoiceVolume을 쓰는 이유는 음량 슬라이더를 무시하지 않기 위해서다.
    // m_isMuted 가드로 정확히 한 번만 동작한다.
    private void RestoreVolume()
    {
        if (!m_isMuted)
            return;

        AudioListener.volume = GameSettings.MasterVolume;
        App.Net.Vivox?.ApplyVoiceVolume();
        m_isMuted = false;
    }
}
