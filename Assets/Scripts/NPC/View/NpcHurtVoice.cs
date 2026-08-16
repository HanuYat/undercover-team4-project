using UnityEngine;

/// <summary>
/// 인간 NPC의 피격 신음 — 맞은 순간 3D로 한 번 운다.
///
/// <b>가해 수단이 아니라 맞은 쪽에 붙는다.</b> 타격음(<see cref="EFx"/> BatonHitFlesh 등)은 때린
/// 무기가 내므로 그 무기에만 묶이지만, 신음은 아픈 쪽이 내는 소리라 모든 피해가 지나는
/// <see cref="NpcHealth.OnHit"/>에 건다. 새 피해원이 NPC에 연결되면 배선 없이 따라온다.
///
/// <b>지금 NPC를 실제로 깎는 것은 진압봉뿐이다.</b> 폭주 차량(#304)은 넉백만 주고
/// (<c>RunawayVehicle.ServerHitNpc</c>), 폭탄은 플레이어만 깎으며(<c>BombDevice</c> 주석의
/// "폭발 피해는 아직 연결하지 않았다"), 낙뢰(#647)는 <see cref="PlayerHealth"/>만 본다.
/// 그래서 오늘의 동작은 타격음과 한 쌍으로 겹쳐 나는 것이고, 위 문단은 그 셋이 연결됐을 때를
/// 위한 준비다 — <b>그때 종족이 소리로 드러나는 범위가 넓어지므로</b>(GDD 6장: 종족은 스캔 전용,
/// 아래 문단 참고) 연결하는 쪽에서 이 컴포넌트를 함께 검토할 것.
///
/// <b>인간에게만 낸다</b> — 안드로이드는 <see cref="CitizenIdentity.IsAndroidBody"/>로 걸러진다.
/// 종족 구분은 이 게임의 정보(스캐너·몽타주)이고, 기계가 사람 소리로 울면 그 구분이 흐려진다.
/// 같은 이유로 플레이어(전원 로봇 경찰, GDD 세계관)에는 이 컴포넌트가 붙지 않는다.
///
/// 3D인 이유는 발소리와 같다 — 어디서 누가 맞고 있는지가 곧 정보다. 본부가 못 보는 구역에서
/// 나는 신음이 "저기서 누가 때리고 있다"를 알린다.
///
/// <b>기절해 있으면 내지 않는다</b> — 쓰러진 몸이 맞을 때마다 우는 것은 읽히지 않고, 무엇보다
/// 이 게임에서 기절은 "이제 묶을 수 있다"는 신호다(<see cref="NpcShockView"/>의 시안과 같은 층).
/// 그 위에 신음이 겹치면 아직 저항 중인 것처럼 들려 신호가 흐려진다.
/// <see cref="NpcStun.IsStunned"/>가 기절 오버레이와 Stunned 상태를 함께 답하므로 그것만 본다.
///
/// 시체는 걸러 낼 필요가 없다 — 죽은 NPC는 <see cref="NpcStateRules.CanBeDamaged"/>에서 피해
/// 자체가 막혀 <see cref="NpcHealth.OnHit"/>이 발행되지 않는다.
/// </summary>
[RequireComponent(typeof(NpcController))]
public class NpcHurtVoice : MonoBehaviour
{
    [Tooltip("신음 사이 최소 간격(초) — 진압봉 연타·폭발 다중 히트에서 소리가 겹쳐 우스워지는 것을 막는다")]
    [Min(0f)]
    [SerializeField] private float m_minInterval = 0.5f;

    private NpcController m_controller;
    private CitizenIdentity m_identity;
    private float m_nextVoiceTime;

    private void Awake()
    {
        m_controller = GetComponent<NpcController>();
        m_identity = GetComponent<CitizenIdentity>();
    }

    private void OnEnable() => m_controller.Health.OnHit += HandleHit;

    private void OnDisable() => m_controller.Health.OnHit -= HandleHit;

    private void HandleHit(DamageHit hit)
    {
        if (m_controller.Stun.IsStunned) return;
        if (m_identity != null && m_identity.IsAndroidBody) return;
        if (Time.time < m_nextVoiceTime) return;

        m_nextVoiceTime = Time.time + m_minInterval;
        App.Sound?.PlaySfxAt(EAudioClip.NpcHurtHuman, transform.position);
    }
}
