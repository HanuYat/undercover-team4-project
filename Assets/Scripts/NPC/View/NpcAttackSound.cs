using System.Collections;
using UnityEngine;

/// <summary>
/// NPC 근접 공격음 (#817) — 공격을 휘두를 때와 실제로 맞혔을 때 각각 3D로 한 번 낸다.
///
/// 판정은 서버가 하지만 두 이벤트 모두 <see cref="NpcReaction"/>이 ClientRpc로 중계하므로
/// 전 피어에서 들린다. <see cref="NpcHurtVoice"/>와 달리 종족을 가리지 않는다 — 스윙은 허공
/// 소리이고, 명중음이 알리는 로봇은 때린 쪽이 아니라 맞은 쪽(플레이어)이다.
///
/// <b>스윙음은 늦춰서 낸다.</b> 스윙 이벤트는 모션 시작에 오는데 공격이 닿는 것은 변형에 따라
/// 0.44~0.73초 뒤라(<see cref="NpcResistConfig.SwingImpactOffset"/>), 시작에 그대로 틀면
/// 휙 소리가 끝나고 한참 뒤에 타격음이 난다.
/// </summary>
[RequireComponent(typeof(NpcController))]
public class NpcAttackSound : MonoBehaviour
{
    [Tooltip("공격이 닿기 이만큼(초) 전에 스윙음을 낸다 — 휙 소리가 타격음으로 이어지게 하는 값. " +
             "0이면 타격과 동시, 크게 잡으면 모션 시작 쪽으로 당겨진다")]
    [Min(0f)]
    [SerializeField] private float m_swingLeadSeconds = 0.18f;

    private NpcController m_controller;

    private void Awake() => m_controller = GetComponent<NpcController>();

    private void OnEnable()
    {
        m_controller.Reaction.OnAttackSwing += HandleSwing;
        m_controller.Reaction.OnAttackHit += HandleHit;
    }

    private void OnDisable()
    {
        m_controller.Reaction.OnAttackSwing -= HandleSwing;
        m_controller.Reaction.OnAttackHit -= HandleHit;

        // 풀에서 재사용되는 몸이라 예약을 남기면 다음 생에 울린다
        StopAllCoroutines();
    }

    private void HandleSwing(int variant)
    {
        float delay = m_controller.ResistConfig.SwingImpactOffset(variant) - m_swingLeadSeconds;
        if (delay <= 0f)
        {
            PlaySwing();
            return;
        }

        StartCoroutine(PlaySwingAfter(delay));
    }

    private IEnumerator PlaySwingAfter(float delay)
    {
        yield return new WaitForSeconds(delay);
        PlaySwing();
    }

    private void PlaySwing() => App.Sound?.PlaySfxAt(EAudioClip.NpcAttackSwing, transform.position);

    private void HandleHit() => App.Sound?.PlaySfxAt(EAudioClip.NpcAttackHitRobot, transform.position);
}
