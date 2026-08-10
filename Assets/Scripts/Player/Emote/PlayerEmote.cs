using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 감정표현 재생 상태 — 서버 권위. (#219)
///
/// <b>이벤트가 아니라 상태로 동기화한다.</b> 일회성 RPC(Baton.PlaySwingRpc 방식)를 쓰지 않는
/// 이유는 이 기능에 <b>취소가 있기</b> 때문이다. 시작과 취소를 각각 RPC로 보내면 하나가
/// 유실되거나 순서가 뒤집혔을 때 남의 화면에서 영원히 춤추는 플레이어가 남고 되돌릴 길이 없다.
/// 취소는 이동 입력마다 일어나므로 드문 사고가 아니다. 상태값 하나면 마지막 값으로 수렴하고,
/// 재생 도중 다가온 피어도 진행 중인 감정표현을 그대로 본다.
///
/// <b>이동 정지 판정을 서버에서 하지 않는다.</b> 이동 권한이 아직 오너에 있어(#55 이전) 서버가
/// 보는 속도가 정확하지 않고, 서버까지 이동을 감지해 끊으면 취소 경로가 둘이 되어 지연 탓에
/// 내 화면보다 남의 화면에서 먼저 끊긴다. 감정표현은 게임 유불리가 0이라 오너를 믿는 비용이
/// 없다 — 서버는 <b>자기가 확실히 아는 것</b>(무력화·앉기·공중·업힘)만 본다.
/// </summary>
[RequireComponent(typeof(PlayerIncapacitation))]
public class PlayerEmote : NetworkBehaviour
{
    /// <summary>재생 중인 감정표현이 없음.</summary>
    public const sbyte k_none = -1;

    [Tooltip("감정표현 목록 — 모든 피어가 같은 에셋을 봐야 한다")]
    [SerializeField]
    private EmoteCatalog m_catalog;

    private readonly NetworkVariable<sbyte> m_activeEmote = new(
        k_none,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private PlayerIncapacitation m_incapacitation;
    private PlayerCrouch m_crouch;
    private PlayerJump m_jump;
    private PlayerCarrier m_carrier;

    // 서버 전용 — 비루프 클립의 자동 종료 시각. 0이면 시간으로 끊지 않는다(루프).
    private float m_autoStopTime;

    public EmoteCatalog Catalog => m_catalog;
    public sbyte ActiveEmote => m_activeEmote.Value;
    public bool IsEmoting => m_activeEmote.Value != k_none;

    /// <summary>재생 대상이 바뀌었다 — 전 피어에서 발화한다. 인자는 새 인덱스(k_none이면 종료).</summary>
    public event Action<sbyte> OnActiveEmoteChanged;

    private void Awake()
    {
        m_incapacitation = GetComponent<PlayerIncapacitation>();
        m_crouch = GetComponent<PlayerCrouch>();
        m_jump = GetComponent<PlayerJump>();
        m_carrier = GetComponent<PlayerCarrier>();
    }

    public override void OnNetworkSpawn()
    {
        m_activeEmote.OnValueChanged += HandleActiveEmoteChanged;

        // 늦게 합류한 피어도 이미 재생 중인 감정표현을 보게 한다 — 값 구독만으로는 이 시점의
        // 값이 전달되지 않는다(변경이 없었으므로).
        if (IsEmoting)
            OnActiveEmoteChanged?.Invoke(m_activeEmote.Value);

        if (IsServer)
            m_incapacitation.OnIncapacitatedChanged += HandleIncapacitatedChanged;
    }

    public override void OnNetworkDespawn()
    {
        m_activeEmote.OnValueChanged -= HandleActiveEmoteChanged;

        if (IsServer && m_incapacitation != null)
            m_incapacitation.OnIncapacitatedChanged -= HandleIncapacitatedChanged;
    }

    /// <summary>감정표현 발동을 요청한다 — 오너가 부른다. 서버가 조건을 보고 결정한다.</summary>
    public void RequestEmote(int index)
    {
        if (!IsOwner)
            return;

        RequestEmoteServerRpc((sbyte)index);
    }

    /// <summary>재생 중인 감정표현을 끊는다 — 오너가 부른다(이동·공격 등).</summary>
    public void CancelEmote()
    {
        if (!IsOwner)
            return;

        CancelEmoteServerRpc();
    }

    [ServerRpc]
    private void RequestEmoteServerRpc(sbyte index)
    {
        if (m_catalog == null || !m_catalog.IsValidIndex(index))
            return;

        if (!CanStartEmote())
            return;

        m_activeEmote.Value = index;

        float duration = m_catalog.Get(index).DurationSeconds;
        m_autoStopTime = duration > 0f ? Time.time + duration : 0f;
    }

    [ServerRpc]
    private void CancelEmoteServerRpc() => ServerStopEmote();

    // 서버가 확실히 아는 조건만 본다 — 이동 정지 여부는 오너가 판정한다(클래스 주석 참고).
    private bool CanStartEmote()
    {
        if (m_incapacitation != null && m_incapacitation.IsIncapacitated)
            return false;

        if (m_crouch != null && m_crouch.IsCrouching)
            return false;

        if (m_jump != null && m_jump.IsAirborne)
            return false;

        if (m_carrier != null && m_carrier.IsBeingCarried)
            return false;

        return true;
    }

    private void Update()
    {
        if (!IsServer || !IsEmoting)
            return;

        // 비루프 클립의 자동 종료. 애니메이터 exit time에 맡기지 않는 이유는 재생 여부의 진실을
        // 이 값 하나로 유지하기 위해서다 — 둘로 나뉘면 서버는 재생 중인데 화면만 멈춘 상태가 난다.
        if (m_autoStopTime > 0f && Time.time >= m_autoStopTime)
        {
            ServerStopEmote();
            return;
        }

        // 서버가 아는 사유로 끊는다. 오너의 취소 요청과 별개로 도는 이유는, 오너가 응답할 수
        // 없는 상황(다운·업힘)에서도 감정표현이 멈춰야 하기 때문이다.
        if (!CanStartEmote())
            ServerStopEmote();
    }

    private void HandleIncapacitatedChanged(bool incapacitated)
    {
        if (incapacitated)
            ServerStopEmote();
    }

    private void ServerStopEmote()
    {
        if (!IsServer || !IsEmoting)
            return;

        m_activeEmote.Value = k_none;
        m_autoStopTime = 0f;
    }

    private void HandleActiveEmoteChanged(sbyte previous, sbyte current)
    {
        OnActiveEmoteChanged?.Invoke(current);
    }
}
