using Unity.Netcode;
using UnityEngine;
using TMPro; // TextMeshPro 사용

/// <summary>
/// 로컬 플레이어의 HP를 좌하단 UI에 표시합니다.
/// PlayerReviveHud의 관례를 따라 오너 전용으로 동작합니다.
/// </summary>
[RequireComponent(typeof(PlayerData))]
public class PlayerHpUI : NetworkBehaviour
{
    [Header("UI 컴포넌트 연결")]
    [Tooltip("에디터에서 생성한 좌하단 TextMeshProUGUI를 드래그하여 연결하세요.")]
    [SerializeField] private TextMeshProUGUI m_hpText;

    private PlayerData m_playerData;

    public override void OnNetworkSpawn()
    {
        // 남의 플레이어 UI가 내 화면에 그려지지 않게 오너 전용으로 설정합니다[cite: 10].
        if (!IsOwner)
        {
            enabled = false; 
            return;
        }

        m_playerData = GetComponent<PlayerData>();
    }

    private void Update()
    {
        // 컴포넌트가 연결되어 있을 때만 매 프레임 UI를 갱신합니다.
        if (m_playerData != null && m_hpText != null)
        {
            // PlayerData에서 퍼블릭으로 열려있는 CurrentHp와 MaxHp 프로퍼티를 읽어옵니다.
            m_hpText.text = $"HP: {m_playerData.CurrentHp} / {m_playerData.MaxHp}";
        }
    }
}