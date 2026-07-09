using UnityEngine;
// using Unity.Netcode; // TODO: 네트워크 테스트 시 주석 해제

public class PlayerData : MonoBehaviour // TODO: 네트워크 테스트 시 NetworkBehaviour로 복구
{
    [Header("스테이터스")]
    [SerializeField] private int m_maxHp = 100;

    // TODO: 네트워크 테스트 시 아래 두 프로퍼티를 NetworkVariable<int>로 교체
    // (지금처럼 로컬 프로퍼티면 다른 클라이언트에 값이 동기화 안 됨)
    public int PlayerId { get; private set; } // TODO: OwnerClientId로 대체 가능한지 검토
    public int MaxHp => m_maxHp;
    public int CurrentHp { get; private set; }

    private void Awake()
    {
        CurrentHp = m_maxHp;
    }

    public void SetPlayerId(int playerId)
    {
        PlayerId = playerId;
    }

    // TODO: 네트워크 테스트 시 서버 권위로만 호출되게 변경 (ServerRpc로 요청 → 서버가 실제 값 변경)
    public void ModifyHp(int delta)
    {
        CurrentHp = Mathf.Clamp(CurrentHp + delta, 0, m_maxHp);
    }
}
