using Unity.Netcode;
using UnityEngine;

public class PlayerData : NetworkBehaviour, IDamageable
{
    [Header("스테이터스")]
    [SerializeField] private int m_maxHp = 100;

    // 서버 권위 HP — 서버만 쓰고 모든 클라이언트가 읽는다.
    // m_hp는 서버·오프라인의 진실값 (NpcController의 상태/게이지 이중 구조와 동일 패턴, #79)
    private readonly NetworkVariable<int> m_syncedHp = new NetworkVariable<int>();
    private int m_hp;

    public ulong PlayerId => OwnerClientId;
    public int MaxHp => m_maxHp;
    public int CurrentHp => IsSpawned ? m_syncedHp.Value : m_hp;

    private void Awake()
    {
        m_hp = m_maxHp; // 오프라인(비네트워크) Play 테스트 폴백 초기값
    }

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            SetHp(m_maxHp);
        }
    }

    // 서버 권위로만 실제 값 변경. (데미지 소스가 클라라면 별도 ServerRpc로 요청)
    public void ModifyHp(int delta)
    {
        if (IsSpawned && !IsServer) return;

        SetHp(Mathf.Clamp(CurrentHp + delta, 0, m_maxHp));
    }

    /// <summary>
    /// 피격 — 저항형 NPC 범위 타격 등 데미지 소스의 공통 경로. (#79)
    /// HP 0 이후의 다운·구조 처리는 GDD 7-5 후속 이슈 — 여기서는 HP만 깎인다.
    /// </summary>
    public void TakeDamage(int amount, GameObject attacker)
    {
        ModifyHp(-amount);
    }

    private void SetHp(int value)
    {
        m_hp = value;
        if (IsSpawned && IsServer)
            m_syncedHp.Value = value;
    }
}
