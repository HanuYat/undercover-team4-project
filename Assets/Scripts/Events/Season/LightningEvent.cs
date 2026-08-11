using System;
using Unity.Netcode;
using UnityEngine;
using Random = UnityEngine.Random;

// GDD 6-4 날씨 이벤트: 번개 (비 내림 포함)
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
    private NetworkVariable<bool> m_lightningSynced = new NetworkVariable<bool>(
        false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.ServerOnly);

    private bool m_lightning = false;
    private float m_endTime;
    private float m_nextStrikeTime;

    public event Action<bool> OnLightningChanged;

    // --- ISuddenEvent 구현 ---
    public string DisplayName => "번개";
    public bool IsActive => m_lightning;
    public bool IsLightningActive => (!IsSpawned || IsServer) ? m_lightning : m_lightningSynced.Value;

    public override void OnNetworkSpawn()
    {
        m_lightningSynced.OnValueChanged += OnSyncValueChanged;
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

    public bool CanTrigger() => true;

    public void ServerBegin()
    {
        m_endTime = Time.time + m_durationSeconds;
        SetLightning(true);
        ScheduleNextStrike();
        Debug.Log($"[LightningEvent] ServerBegin. Ends at {m_endTime}s.");
    }

    public void ServerTick()
    {
        if (!m_lightning) return;

        if (Time.time >= m_endTime)
        {
            SetLightning(false);
            Debug.Log("[LightningEvent] ServerEnd by duration.");
            return;
        }

        if (Time.time >= m_nextStrikeTime)
        {
            PerformLightningStrike();
            ScheduleNextStrike();
        }
    }

    public void ServerReset()
    {
        SetLightning(false);
    }

    private void SetLightning(bool value)
    {
        if (m_lightning == value) return;
        m_lightning = value;

        if (IsServer)
        {
            m_lightningSynced.Value = value;
        }
        OnLightningChanged?.Invoke(value);
    }

    private void ScheduleNextStrike()
    {
        m_nextStrikeTime = Time.time + Random.Range(m_strikeIntervalMin, m_strikeIntervalMax);
    }

    // --- 핵심 낙뢰 로직 (서버) ---
    private void PerformLightningStrike()
    {
        if (!IsServer) return;

        // PlayerState로 변경
        PlayerState targetPlayer = GetRandomPlayerField();
        if (targetPlayer == null) return;

        Vector3 strikePosition = targetPlayer.transform.position;
        ulong targetClientId = targetPlayer.OwnerClientId;

        bool isDamage = Random.value < m_damageChance;

        if (isDamage)
        {
            ApplyDamage(targetPlayer);
            Debug.Log($"[LightningEvent] Strike DAMAGE on Player {targetClientId} at {strikePosition}");
        }
        else
        {
            ApplySpeedBuff(targetPlayer);
            Debug.Log($"[LightningEvent] Strike BUFF on Player {targetClientId} at {strikePosition}");
        }

        PlayStrikeVFXClientRpc(strikePosition);
    }

    private PlayerState GetRandomPlayerField()
    {
        // 실제 게임 환경의 플레이어 관리자 로직이 들어갈 곳
        // 임시 방편으로 현재 씬에 있는 PlayerState 중 랜덤으로 하나 반환
        PlayerState[] allPlayers = FindObjectsByType<PlayerState>(FindObjectsSortMode.None);
        if (allPlayers.Length > 0)
        {
            return allPlayers[Random.Range(0, allPlayers.Length)];
        }
        
        Debug.LogWarning("[LightningEvent] No PlayerState found. Strike will fail.");
        return null; 
    }

    private void ApplyDamage(PlayerState player)
    {
        var health = player.GetComponent<PlayerHealth>();
        if (health != null)
        {
            // TODO: 실제 PlayerHealth의 데미지 API 주석 해제 및 수정
            // health.TakeDamageServer(m_damageAmount);
            Debug.Log($"[LightningEvent] ApplyDamage {m_damageAmount} to {player.name}");
        }
    }

    private void ApplySpeedBuff(PlayerState player)
    {
        var movement = player.GetComponent<PlayerMovement>();
        if (movement != null)
        {
            // TODO: 실제 PlayerMovement의 이속 버프 API 주석 해제 및 수정
            // movement.ApplySpeedBuffServer(m_buffMultiplier, m_buffDuration);
            Debug.Log($"[LightningEvent] ApplySpeedBuff x{m_buffMultiplier} for {m_buffDuration}s to {player.name}");
        }
    }

    [ClientRpc]
    private void PlayStrikeVFXClientRpc(Vector3 position)
    {
        if (IsServer && !IsHost) return; 
        LightningView.Instance?.PlayStrikeEffects(position);
    }
}
