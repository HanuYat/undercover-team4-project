using UnityEngine;

/// <summary>
/// 컨트롤러 레벨 공용 튜닝 값 — 특정 FSM 상태에 속하지 않는다. (#259 — NpcController에서 분리)
/// 스폰 시 개체 편차, 넉백 비행 물리(#232), 체력(#366)을 담는다.
/// </summary>
[CreateAssetMenu(fileName = "NpcCommonConfig", menuName = "Undercover/NPC/Common Config")]
public class NpcCommonConfig : ScriptableObject
{
    [Header("개체별 이동 속도 편차 (배율)")]
    [Tooltip("스폰 시 NavMeshAgent 속도에 이 범위의 랜덤 배율을 곱한다 — 군중이 전부 같은 속도로 걷는 것 방지")]
    [SerializeField] private float m_spawnSpeedMultiplierMin = 0.8f;
    [SerializeField] private float m_spawnSpeedMultiplierMax = 1.2f;

    [Header("넉백 (폭발 등 외력) — #232")]
    [Tooltip("날아가는 동안 받는 중력. 음수 — 클수록 낮고 빠르게 떨어진다")]
    [SerializeField] private float m_knockbackGravity = -18f;
    [Tooltip("안전장치: 이 시간(초)이 지나도 착지 판정이 안 나면 강제로 내려놓는다")]
    [SerializeField] private float m_knockbackMaxFlightSeconds = 3f;
    [Tooltip("착지 지점을 NavMesh 위로 되돌릴 때 허용하는 최대 탐색 거리(m)")]
    [SerializeField] private float m_knockbackLandSampleDistance = 4f;
    [Tooltip("날아가는 도중 벽으로 칠 콜라이더 — 여기에 걸리면 수평 이동이 멈춘다. NPC 자신의 레이어는 런타임에 자동으로 빠진다")]
    [SerializeField] private LayerMask m_knockbackObstacleMask = 1; // Default(환경)만 — 캐릭터 오판 방지 (#339)

    [Header("무게 (밧줄 끌기 속도 페널티) — #398")]
    [Tooltip("스폰 시 이 중 하나를 균등 추첨해 개체 무게로 삼는다(경량/표준/중량). 끄는 플레이어의 이동속도가 이 값에 비례해 떨어진다 — 페널티 계수·하한은 RopeDragLoad에 있다. 비어 있으면 전원 1.0")]
    [SerializeField] private float[] m_weightTiers = { 0.6f, 1f, 1.6f };

    [Header("체력 — #366/#916")]
    [Tooltip("NPC 최대 체력 — 0이 되면 쓰러진다(기절). 저항 제압 게이지(구 SubdueGaugeMax)를 대체한 값")]
    [SerializeField] private int m_maxHp = 100;

    [Tooltip("한 방의 <b>초과</b> 피해(피해량 − 남은 체력)가 이 값 이상이면 기절을 건너뛰고 즉사한다 — " +
             "뿅망치 1% 대박(9999)·홈런 진압봉·차량·폭발이 설계대로 죽이게 하는 예외다(#916). " +
             "진압봉 한 대(34)로 마지막 체력을 깎는 것은 초과량이 작아 걸리지 않는다")]
    [Min(1)]
    [SerializeField] private int m_lethalOverkillHp = 100;
    // 제압 타격량(m_subdueHitPower)은 제거됐다 (#438) — 유일한 소비처였던 E 제압 타격이 사라졌다.
    // 진압봉은 자기 Baton.m_damage(같은 34)를 쓴다 — 무기 수치는 무기가 들고 있는 편이 맞다.

    [Header("방치 회복 — #707")]
    [Tooltip("마지막 피해로부터 이 시간(초)이 지나야 회복이 시작된다. 그 전에 다시 맞으면 처음부터 다시 잰다")]
    [SerializeField] private float m_regenDelaySeconds = 20f;
    [Tooltip("회복 속도(초당 HP) — MaxHp까지 오른다. 팀 결정(2026-08-19, #707): 상한을 묶지 않고 반복 넉다운을 허용한다")]
    [SerializeField] private float m_regenHpPerSecond = 2f;

    public float SpawnSpeedMultiplierMin => m_spawnSpeedMultiplierMin;
    public float SpawnSpeedMultiplierMax => m_spawnSpeedMultiplierMax;
    public float KnockbackGravity => m_knockbackGravity;
    public float KnockbackMaxFlightSeconds => m_knockbackMaxFlightSeconds;
    public float KnockbackLandSampleDistance => m_knockbackLandSampleDistance;
    public LayerMask KnockbackObstacleMask => m_knockbackObstacleMask;

    /// <summary>
    /// 개체 무게를 추첨한다 — 스폰 초기화에서 1회. 서버(또는 오프라인)에서만 호출된다. (#398)
    /// 프리팹 고정값이 아니라 추첨인 이유: 시민이 NPC의 대다수라 프리팹으로 가르면 "무거운 놈 vs
    /// 가벼운 놈"이 생기지 않는다. 위 이동속도 랜덤 배율과 같은 자리·같은 방식이다.
    /// </summary>
    public float PickWeight() =>
        m_weightTiers.Length == 0 ? 1f : m_weightTiers[Random.Range(0, m_weightTiers.Length)];

    /// <summary>NPC 최대 체력 — HUD가 비율 계산에, NpcController가 초기화·회복에 읽는다. (#366)</summary>
    public int MaxHp => m_maxHp;

    /// <summary>즉사 오버킬 임계(HP) — 한 방의 <b>초과</b> 피해가 이 값 이상이면 기절 없이 죽는다.
    /// 한 방에 크게 넘기는 수단(뿅망치 대박·홈런·차량·폭발)이 눕히기만 하게 되는 것을 막는 예외다. (#916)</summary>
    public int LethalOverkillHp => m_lethalOverkillHp;

    /// <summary>방치 회복이 시작되기까지의 대기 시간(초) — NpcHealth가 마지막 피해 이후 잰다. (#707)</summary>
    public float RegenDelaySeconds => m_regenDelaySeconds;

    /// <summary>방치 회복 속도(초당 HP) — NpcHealth가 MaxHp까지 적용한다. (#707)</summary>
    public float RegenHpPerSecond => m_regenHpPerSecond;
}
