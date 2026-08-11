using System;
using Unity.Netcode;
using UnityEngine;
using Random = UnityEngine.Random;

// GDD 6-4 날씨 이벤트: 번개
[RequireComponent(typeof(SuddenEventManager))]
public class LightningEvent : NetworkBehaviour, ISuddenEvent
{
    // --- 인스펙터 노출 수치 ---
    [Header("Settings")]
    [SerializeField] private float m_durationSeconds = 15f;    // 이벤트 총 지속 시간
    
    [Header("Strike Interval (Seconds)")]
    [SerializeField] private float m_strikeIntervalMin = 2f; // 낙뢰 최소 주기
    [SerializeField] private float m_strikeIntervalMax = 5f; // 낙뢰 최대 주기

    [Header("Strike Effects")]
    [Range(0f, 1f)]
    [SerializeField] private float m_damageChance = 0.5f;   // 피해 발생 확률 (나머지는 버프)
    [SerializeField] private int m_damageAmount = 1;        // 피해량
    [SerializeField] private float m_buffMultiplier = 1.5f;  // 이속 버프 배수
    [SerializeField] private float m_buffDuration = 5f;      // 버프 지속 시간

    // --- 상태 및 동기화 ---
    // 클라이언트 표현용 동기화 변수 (Server 권한 최신 NGO 문법 적용 완료)
    private NetworkVariable<bool> m_lightningSynced = new NetworkVariable<bool>(
        false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // 서버/오프라인 진실값
    private bool m_lightning = false;
    private float m_endTime;
    private float m_nextStrikeTime;

    // 클라이언트 표현 컴포넌트 구독용 이벤트
    public event Action<bool> OnLightningChanged;

    // --- ISuddenEvent 구현 ---
    public string DisplayName => "번개";
    public bool IsActive => m_lightning;

    // 클라이언트에서 현재 번개 상태 조회
    public bool IsLightningActive => (!IsSpawned || IsServer) ? m_lightning : m_lightningSynced.Value;

    public override void OnNetworkSpawn()
    {
        m_lightningSynced.OnValueChanged += OnSyncValueChanged;
        
        // Late-join 처리
        if (m_lightningSynced.Value)
        {
            OnLightningChanged?.Invoke(true);
        }
    }

    public override void OnNetworkDespawn()
    {
        m_lightningSynced.OnValueChanged -= OnSyncValueChanged;
    }

    private void OnSyncValueChanged(bool previousValue, bool newValue)
    {
        OnLightningChanged?.Invoke(newValue);
    }

    // --- 서버 로직 (ISuddenEvent) ---

    public bool CanTrigger() => true;

    public void ServerBegin()
    {
        m_endTime = Time.time + m_durationSeconds;
        SetLightning(true);
        
        // 첫 낙뢰 시간 예약
        ScheduleNextStrike();
        Debug.Log($"[LightningEvent] ServerBegin. Ends at {m_endTime}s.");
    }

    public void ServerTick()
    {
        if (!m_lightning) return;

        // 지속 시간 종료 체크
        if (Time.time >= m_endTime)
        {
            SetLightning(false);
            Debug.Log("[LightningEvent] ServerEnd by duration.");
            return;
        }

        // 주기적 낙뢰 발생 처리
        if (Time.time >= m_nextStrikeTime)
        {
            PerformLightningStrike();
            ScheduleNextStrike();
        }
    }

    // 라운드 종료 강제 정리
    public void ServerReset()
    {
        SetLightning(false);
    }

    private void SetLightning(bool value)
    {
        if (m_lightning == value) return;

        m_lightning = value;

        // 서버 전용: NetworkVariable 갱신 -> 클라 동기화
        if (IsServer)
        {
            m_lightningSynced.Value = value;
        }

        // 로컬(서버 or 오프라인) 콜백
        OnLightningChanged?.Invoke(value);
    }

    private void ScheduleNextStrike()
    {
        m_nextStrikeTime = Time.time + Random.Range(m_strikeIntervalMin, m_strikeIntervalMax);
    }

    // --- 핵심 낙뢰 로직 (서버 권위) ---
    private void PerformLightningStrike()
    {
        if (!IsServer) return;

        // 1. 대상 선정: 씬의 PlayerHealth 중 무작위 1명
        PlayerHealth target = GetRandomPlayerField();
        
        if (target == null) return; // 대상 없으면 패스

        Vector3 strikePosition = target.transform.position;

        // 2. 효과 롤 (Random.value < m_damageChance)
        bool isDamage = Random.value < m_damageChance;

        if (isDamage)
        {
            ApplyDamage(target);
            Debug.Log($"[LightningEvent] Strike DAMAGE at {strikePosition}");
        }
        else
        {
            ApplySpeedBuff(target);
            Debug.Log($"[LightningEvent] Strike BUFF at {strikePosition}");
        }

        // 3. VFX 표현: 전 클라에 RPC 전송
        PlayStrikeVFXClientRpc(strikePosition);
    }

    // PlayerHealth 컴포넌트를 기반으로 씬에 있는 플레이어를 무작위로 찾습니다.
    private PlayerHealth GetRandomPlayerField()
    {
        PlayerHealth[] players = FindObjectsByType<PlayerHealth>(FindObjectsSortMode.None);
        
        if (players == null || players.Length == 0) return null;
        
        return players[Random.Range(0, players.Length)];
    }

    private void ApplyDamage(PlayerHealth healthComponent)
    {
        if (healthComponent != null)
        {
            // TODO: 실제 프로젝트의 PlayerHealth 데미지 적용 API 호출 (주석 해제 후 이름 맞추기)
            // healthComponent.TakeDamageServer(m_damageAmount);
        }
    }

    private void ApplySpeedBuff(PlayerHealth healthComponent)
    {
        // PlayerHealth와 동일한 오브젝트에 붙어있는 PlayerMovement를 가져옵니다.
        var movement = healthComponent.GetComponent<PlayerMovement>();
        if (movement != null)
        {
            // TODO: 실제 프로젝트의 PlayerMovement 버프 적용 API 호출 (주석 해제 후 이름 맞추기)
            // movement.ApplySpeedBuffServer(m_buffMultiplier, m_buffDuration);
        }
    }

    // --- 표현 RPC ---
    [ClientRpc]
    private void PlayStrikeVFXClientRpc(Vector3 position)
    {
        // 서버는 perform 로직에서 이미 로그를 찍었으므로, 호스트/클라 표현만 처리
        if (IsServer && !IsHost) return; 

        // LightningView를 통해 섬광 및 파티클 재생 명령
        LightningView.Instance?.PlayStrikeEffects(position);
    }
}