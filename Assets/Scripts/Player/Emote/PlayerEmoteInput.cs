using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 감정표현 입력 — 휠 열기·조준·발동·취소. 오너 전용. (#219)
///
/// <b>조준을 마우스 위치가 아니라 델타 누적으로 하는 이유:</b> 게임 중에는 커서가 화면 중앙에
/// 잠겨 있어(CursorLock) 절대 좌표를 읽을 수 없다. 휠을 여는 동안만 커서를 푸는 방법도 있지만,
/// 그러면 화면 밖으로 커서가 나가거나 다른 창으로 포커스가 새는 길이 열린다.
///
/// <b>취소를 여기서 판정하는 이유:</b> 이동 정지 여부는 오너가 가장 정확히 안다(이동 권한이
/// 오너에 있다). 서버까지 감지해 끊으면 취소 경로가 둘이 되어 남의 화면에서 먼저 끊긴다.
/// 서버는 오너가 응답할 수 없는 사유(무력화·업힘)만 본다 — PlayerEmote 참고.
/// </summary>
[RequireComponent(typeof(PlayerEmote))]
[RequireComponent(typeof(PlayerInputHandler))]
public class PlayerEmoteInput : MonoBehaviour
{
    // 휠 조준 감도 — 마우스 델타(픽셀)를 정규화 반경 1로 채우는 데 필요한 픽셀 수.
    // 작을수록 조금만 움직여도 끝까지 간다.
    private const float k_aimPixelsToFull = 220f;

    // 이 이상 이동 입력이 들어오면 취소한다. 0으로 두면 스틱 드리프트·키 채터링에도 끊긴다.
    private const float k_moveCancelThreshold = 0.2f;

    [SerializeField]
    private EmoteWheelView m_wheelView;

    private PlayerEmote m_emote;
    private PlayerInputHandler m_inputHandler;
    private PlayerLook m_look; // 휠 조준 중 시점 회전 정지 (#219)

    // 저장 칸이 계정별로 갈리므로 PlayerId를 알 수 있는 Awake에서 만든다 (#640)
    private EmoteLoadout m_slots;
    private Vector2 m_aim;

    /// <summary>휠이 열려 있는가 — 휠 UI와 취소 판정이 본다.</summary>
    public bool IsWheelOpen { get; private set; }

    /// <summary>지금 가리키는 방향(정규화, 크기 0~1) — 휠 UI가 강조 표시에 쓴다.</summary>
    public Vector2 WheelDirection => m_aim;

    private void Awake()
    {
        m_emote = GetComponent<PlayerEmote>();
        m_inputHandler = GetComponent<PlayerInputHandler>();
        m_look = GetComponent<PlayerLook>();
        m_slots = new EmoteLoadout(App.Net.Auth != null ? App.Net.Auth.PlayerId : null);
        m_slots.Load();
        FillDefaultSlotsIfEmpty();
    }

    /// <summary>
    /// 저장된 구성이 하나도 없으면 카탈로그 앞에서부터 8칸을 채운다. (#219)
    ///
    /// 로비에서 한 번도 구성하지 않은 플레이어에게 <b>빈 휠</b>을 보여 주지 않기 위한 것이다.
    /// 빈 휠은 "아직 안 골랐다"가 아니라 "기능이 고장 났다"로 읽힌다 — 눌러도 아무 일이 없으니
    /// 원인을 짐작할 단서가 화면에 없다.
    ///
    /// 일부만 채운 구성은 건드리지 않는다. 칸을 <b>일부러 비워 둔 것</b>도 사용자의 선택이고,
    /// 여기서 메워 버리면 로비에서 지운 감정표현이 되살아난다.
    /// </summary>
    private void FillDefaultSlotsIfEmpty()
    {
        EmoteCatalog catalog = m_emote.Catalog;
        if (catalog == null)
            return;

        for (int slot = 0; slot < EmoteLoadout.k_slotCount; slot++)
        {
            if (!string.IsNullOrEmpty(m_slots.GetSlot(slot)))
                return; // 하나라도 채워져 있으면 사용자 구성이다
        }

        int count = Mathf.Min(EmoteLoadout.k_slotCount, catalog.Count);
        for (int slot = 0; slot < count; slot++)
        {
            EmoteDefinition definition = catalog.Get(slot);
            if (definition != null)
                m_slots.SetSlot(slot, definition.Id);
        }
    }

    private void OnEnable()
    {
        m_inputHandler.OnEmoteWheelOpened += OpenWheel;
        m_inputHandler.OnEmoteWheelClosed += CloseWheelAndFire;
    }

    private void OnDisable()
    {
        m_inputHandler.OnEmoteWheelOpened -= OpenWheel;
        m_inputHandler.OnEmoteWheelClosed -= CloseWheelAndFire;

        if (IsWheelOpen)
            CloseWheel();
    }

    private void Update()
    {
        if (IsWheelOpen)
        {
            AccumulateAim();
            return;
        }

        if (m_emote.IsEmoting && ShouldCancel())
            m_emote.CancelEmote();
    }

    private void OpenWheel()
    {
        if (m_emote.IsEmoting)
            m_emote.CancelEmote(); // 갈아타기: 새로 고르는 동안 이전 것은 끊는다

        // 조준하는 동안 화면은 고정한다 — 같은 마우스 이동이 칸 선택이라 시점까지 돌면 둘이 겹친다.
        // 열림 래치를 먼저 보는 것은 Push/Pop 짝을 지키기 위해서다.
        if (!IsWheelOpen && m_look != null)
            m_look.PushLookSuspend();

        IsWheelOpen = true;
        m_aim = Vector2.zero;

        if (m_wheelView != null)
            m_wheelView.Open(m_slots, m_emote.Catalog, this);
    }

    private void CloseWheelAndFire()
    {
        if (!IsWheelOpen)
            return;

        int slot = EmoteWheelGeometry.SlotFromDirection(m_aim);
        CloseWheel();

        if (slot < 0)
            return; // 데드존 — 아무것도 고르지 않고 뗀 것

        string emoteId = m_slots.GetSlot(slot);
        if (string.IsNullOrEmpty(emoteId))
            return; // 빈 칸

        EmoteCatalog catalog = m_emote.Catalog;
        if (catalog == null)
            return;

        // id → 인덱스. 저장된 id가 지금 카탈로그에 없으면(에셋이 빠졌다) 조용히 넘어간다.
        int index = catalog.IndexOf(emoteId);
        if (index < 0)
            return;

        if (!CanStartHere())
            return;

        m_emote.RequestEmote(index);
    }

    private void CloseWheel()
    {
        // 발동하든 데드존으로 취소하든 닫는 길은 이 하나뿐이라 어느 경로로 나가도 짝이 맞는다.
        if (IsWheelOpen && m_look != null)
            m_look.PopLookSuspend();

        IsWheelOpen = false;

        if (m_wheelView != null)
            m_wheelView.Close();
    }

    private void AccumulateAim()
    {
        if (Mouse.current == null)
            return;

        m_aim += Mouse.current.delta.ReadValue() / k_aimPixelsToFull;
        m_aim = Vector2.ClampMagnitude(m_aim, 1f);
    }

    // 오너가 아는 시작 조건 — 서버가 보는 것(무력화·앉기·공중·업힘)과 겹치지 않는 부분만 본다.
    private bool CanStartHere() => m_inputHandler.MoveInput.magnitude <= k_moveCancelThreshold;

    // 재생 중 취소 사유. 서버가 아는 것(무력화·업힘)은 서버가 따로 끊으므로 여기서 보지 않는다.
    private bool ShouldCancel() => m_inputHandler.MoveInput.magnitude > k_moveCancelThreshold;
}
