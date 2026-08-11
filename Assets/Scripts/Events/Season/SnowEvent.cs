using System;
using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(SuddenEventManager))]
public class SnowEvent : NetworkBehaviour, ISuddenEvent
{
    [Header("Snow Settings")]
    [SerializeField] private float m_durationSeconds = 30f;

    // 동기화 플래그 및 서버/오프라인용 진실값
    private NetworkVariable<bool> m_snowSynced = new NetworkVariable<bool>(false);
    private bool m_snow;
    private float m_endTime;

    public string DisplayName => "눈";
    public bool IsActive => m_snow;
    
    // 원격 클라이언트면 동기화 값을, 서버(또는 오프라인)면 진실값을 사용
    public bool IsSnow => (!IsSpawned || IsServer) ? m_snow : m_snowSynced.Value;

    public event Action<bool> OnSnowChanged;

    public override void OnNetworkSpawn()
    {
        m_snowSynced.OnValueChanged += (prev, current) => OnSnowChanged?.Invoke(current);
        
        // Late-join 처리: 이미 눈이 내리고 있다면 즉시 반영
        if (m_snowSynced.Value)
        {
            OnSnowChanged?.Invoke(true);
        }
    }

    public override void OnNetworkDespawn()
    {
        m_snowSynced.OnValueChanged -= (prev, current) => OnSnowChanged?.Invoke(current);
    }

    public bool CanTrigger() => true;

    public void ServerBegin()
    {
        m_endTime = Time.time + m_durationSeconds;
        SetSnow(true);
    }

    public void ServerTick()
    {
        if (m_snow && Time.time >= m_endTime)
        {
            SetSnow(false);
        }
    }

    public void ServerReset()
    {
        SetSnow(false);
    }

    private void SetSnow(bool value)
    {
        if (m_snow == value) return;

        m_snow = value;
        if (IsSpawned && IsServer)
        {
            m_snowSynced.Value = value;
        }
        
        OnSnowChanged?.Invoke(value);
    }
}