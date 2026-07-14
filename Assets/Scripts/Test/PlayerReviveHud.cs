using Unity.Netcode;
using UnityEngine;

/// <summary>
/// [임시] 구조 상호작용 온스크린 프롬프트. (#105)
/// 오너 화면에만 표시: 다운된 아군을 조준하면 구조 키 안내를, 내가 다운되면 대기 메시지를 띄운다.
/// NetworkBootstrap·PlayerMovement의 임시 OnGUI 관례를 따른다 — 정식 상호작용 UI(#65 계열)로 대체 예정.
/// </summary>
[RequireComponent(typeof(PlayerReviver))]
public class PlayerReviveHud : NetworkBehaviour
{
    private PlayerInputHandler m_inputHandler;
    private PlayerReviver m_reviver;
    private PlayerIncapacitation m_incapacitation;

    public override void OnNetworkSpawn()
    {
        if (!IsOwner)
        {
            enabled = false; // 남의 플레이어 것이 내 화면에 그려지지 않게 (오너 전용 HUD)
            return;
        }

        m_inputHandler = GetComponent<PlayerInputHandler>();
        m_reviver = GetComponent<PlayerReviver>();
        m_incapacitation = GetComponent<PlayerIncapacitation>();
    }

    private void OnGUI()
    {
        // 내가 다운된 경우 — 구조 대기 메시지
        if (m_incapacitation != null && m_incapacitation.IsIncapacitated)
        {
            DrawCenterLabel("다운됨 — 동료의 구조를 기다리는 중...");
            return;
        }

        // 다운된 아군을 조준 중이면 구조 키 프롬프트
        if (m_reviver != null && m_reviver.CurrentReviveTarget != null)
        {
            string key = m_inputHandler != null ? m_inputHandler.InteractDisplayName : "?";
            DrawCenterLabel($"[E]키를 홀드하여 구조");
        }
    }

    // 화면 중앙 하단에 가독성용 반투명 배경과 함께 라벨을 그린다.
    private static void DrawCenterLabel(string text)
    {
        const float width = 420f;
        const float height = 44f;
        Rect rect = new Rect((Screen.width - width) * 0.5f, Screen.height * 0.62f, width, height);

        Color prev = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.5f);
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = prev;

        GUIStyle style = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 20,
            fontStyle = FontStyle.Bold
        };
        style.normal.textColor = Color.white;
        GUI.Label(rect, text, style);
    }
}
