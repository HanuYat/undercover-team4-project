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
    private PlayerCarrier m_carrier; // 운반 프롬프트 (#365)

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
        m_carrier = GetComponent<PlayerCarrier>();
    }

    private void OnGUI()
    {
        // 라운드 정산 화면이 떠 있으면 그 위로 겹쳐 그리지 않는다.
        // (전원 다운으로 라운드가 끝나면 나는 여전히 무력화 상태라 아래 "다운됨" 메시지가 정산 위로 샌다)
        if (App.UI.Current != null
            && App.UI.Current.TryGetPanel(out SettlementPanel settlement)
            && settlement.IsOpened)
            return;

        // 내가 다운된 경우 — 구조 대기 메시지 + Die까지 남은 시간.
        // IsIncapacitated가 아니라 IsDowned를 본다 (#252): 기절·오검거 매달기도 무력화지만 스스로
        // 풀리므로 구조를 기다리라는 안내가 거짓이 된다. 아무도 오지 않는데 기다리게 만든다.
        if (m_incapacitation != null && m_incapacitation.IsDowned)
        {
            // 남은 시간을 함께 보여준다 (#364) — 안 보이면 기다리다 갑자기 기능 정지로 떨어진다
            int remaining = Mathf.CeilToInt(m_incapacitation.RemainingUntilDie);
            DrawCenterLabel($"다운됨 — 동료의 구조를 기다리는 중... (기능 정지까지 {remaining}초)");
            return;
        }

        // 내가 기능 정지(Die)된 경우 — 구조는 끝났고 본부 이송(#365)만 남았다는 안내 (#364)
        if (m_incapacitation != null && m_incapacitation.IsDead)
        {
            DrawCenterLabel("기능 정지 — 동료가 본부로 이송해야 복구된다");
            return;
        }

        // 동료를 운반 중 — 목적지와 내려놓기 안내 (#365)
        if (m_carrier != null && m_carrier.IsCarrying)
        {
            DrawCenterLabel("운반 중 — 본부 부활 장치를 겨냥해 [E]로 안치 (그 외 [E]는 내려놓기)");
            return;
        }

        // 다운된 아군을 조준 중이면 구조 키 프롬프트
        if (m_reviver != null && m_reviver.CurrentReviveTarget != null)
        {
            DrawCenterLabel($"[E]키를 홀드하여 구조");
            return;
        }

        // 기능 정지된 아군을 조준 중 — 구조가 아니라 운반이 답이다 (#364/#365)
        if (m_reviver != null && m_reviver.CurrentDeadTarget != null)
        {
            DrawCenterLabel("기능 정지 — 구조 불가. 밧줄을 들고 좌클릭해 본부로 이송");
        }
    }

    // 라벨 스타일 — OnGUI는 프레임당 여러 번(Layout·Repaint) 돌기 때문에 매번 새로 만들지 않는다.
    // GUI.skin은 OnGUI 안에서만 유효하므로 여기(호출부)에서 지연 생성한다.
    private static GUIStyle s_labelStyle;

    // 화면 중앙 하단에 가독성용 반투명 배경과 함께 라벨을 그린다.
    // 폭·높이는 문구를 <b>재서</b> 잡는다 — 고정 폭이면 긴 안내(운반·이송)가 잘린다.
    private static void DrawCenterLabel(string text)
    {
        const float paddingX = 24f;
        const float paddingY = 12f;
        const float maxWidthRatio = 0.9f; // 좁은 창에서도 화면 밖으로 나가지 않게

        if (s_labelStyle == null)
        {
            s_labelStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 20,
                fontStyle = FontStyle.Bold
            };
            s_labelStyle.normal.textColor = Color.white;
        }

        var content = new GUIContent(text);

        // 한 줄 기준으로 먼저 잰다 — wordWrap을 켜 둔 채 CalcSize를 부르면 결과가 흔들려서,
        // 끈 상태로 재고 화면 폭을 넘길 때만 접는다.
        s_labelStyle.wordWrap = false;
        float maxWidth = Screen.width * maxWidthRatio - paddingX * 2f;
        float singleLineWidth = s_labelStyle.CalcSize(content).x;

        float textWidth = Mathf.Min(singleLineWidth, maxWidth);
        if (singleLineWidth > maxWidth)
            s_labelStyle.wordWrap = true; // 그래도 안 들어가면 두 줄로 접는다 — 잘리는 것보다 낫다

        float textHeight = s_labelStyle.CalcHeight(content, textWidth);

        float width = textWidth + paddingX * 2f;
        float height = textHeight + paddingY * 2f;
        Rect rect = new Rect((Screen.width - width) * 0.5f, Screen.height * 0.62f, width, height);

        Color prev = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.5f);
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = prev;

        GUI.Label(rect, content, s_labelStyle);
    }
}
