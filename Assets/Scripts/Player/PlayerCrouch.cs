using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 플레이어 앉기(crouch) 상태를 서버 권위로 관리한다. (#236)
/// 오너는 Left Ctrl 홀드 입력을 ServerRpc로 전달만 하고, 앉기 여부는 서버가 판정해
/// <see cref="NetworkVariable{T}"/>로 전파한다 — 홀드 방식이라 "떼기" 입력이 유실돼도
/// 상태가 서버에 있어 계속 앉은 채로 남지 않는다. (PlayerIncapacitation과 동일한 권위 패턴)
///
/// 애니메이션(PlayerAnimationDriver)·이동 속도·카메라 높이(PlayerMovement)는 이 컴포넌트의
/// <see cref="IsCrouching"/>/<see cref="CrouchBlend"/>를 읽어 각자 반응한다.
/// 컴포넌트 자체는 원격 피어에서도 계속 돌아 CharacterController 높이를 함께 줄인다.
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class PlayerCrouch : NetworkBehaviour
{
    /// <summary>서기↔앉기 블렌딩 시간(초). 애니메이터 전환 시간과 같은 값을 써야 콜라이더가 모션과 함께 내려간다.</summary>
    public const float k_blendDuration = 0.12f;

    [Header("앉기")]
    [Tooltip("앉았을 때 CharacterController 높이(m). 서기 높이는 프리팹 값에서 캡처한다.")]
    [SerializeField]
    private float m_crouchHeight = 1.2f;

    // 서버 권위 앉기 플래그 — 서버만 쓰고 모든 클라가 읽는다. (PlayerIncapacitation.m_isIncapacitatedSynced와 동일 패턴)
    private readonly NetworkVariable<bool> m_isCrouchingSynced = new NetworkVariable<bool>();
    private bool m_isCrouching; // 서버·오프라인의 진실값 (비네트워크 Play 테스트 폴백)
    private bool m_crouchRequested; // 오너가 보낸 Ctrl 홀드 여부 — 서버에만 의미 있음

    // 눌림 여부 자체도 전파한다 — 공중에서는 실제 앉기가 보류되지만 자세는 웅크려야 하고,
    // 그 자세는 남의 화면에도 보여야 한다. (#189)
    private readonly NetworkVariable<bool> m_isCrouchRequestedSynced = new NetworkVariable<bool>();
    private bool m_isCrouchRequested;

    private CharacterController m_controller;
    private PlayerInputHandler m_inputHandler;
    private PlayerIncapacitation m_incapacitation; // 다운 중엔 앉기 해제 (콜라이더가 줄어든 채 고착되는 것 방지)
    private PlayerJump m_jump; // 공중에서는 실제 앉기를 보류한다 (#189)
    private float m_standHeight; // 서기 CharacterController 높이 — 프리팹 초기값에서 캡처
    private float m_standCenterY; // 서기 CharacterController 중심 y — 발 위치 고정 계산 기준
    private float m_crouchBlend; // 0 = 완전히 섬, 1 = 완전히 앉음

    /// <summary>
    /// <b>앉기 상태</b>인지 — 이동 속도가 따르는 값. 공중에서는 키를 누르고 있어도 false이고,
    /// 착지해야 걸린다. 덕분에 앞으로 뛰다가 공중에서 앉아도 체공 중 이동이 느려지지 않는다.
    /// 콜라이더·카메라는 이 값이 아니라 <see cref="IsCrouchRequested"/>를 따른다는 점에 주의. (#189)
    /// 서버·오프라인은 실참조, 원격 피어는 동기화값으로 판정. (PlayerIncapacitation.IsIncapacitated 관례)
    /// </summary>
    public bool IsCrouching => IsSpawned && !IsServer ? m_isCrouchingSynced.Value : m_isCrouching;

    /// <summary>
    /// 앉기 키를 누르고 있는지 — <b>자세(애니메이션)와 콜라이더·카메라</b>가 따르는 값.
    /// 공중에서 키를 누르면 <see cref="IsCrouching"/>은 false여도 이 값이 true라, 웅크린 자세와
    /// 줄어든 콜라이더가 함께 간다. 보이는 몸과 실제 충돌 크기를 어긋나지 않게 하려는 것이다.
    /// (그 대가로 공중에서 콜라이더가 작아지는 크라우치 점프가 가능하다 — 의도된 선택) (#189)
    ///
    /// 순수 로컬 판단이라 오너는 서버 왕복을 기다리지 않는다 — 체공이 0.7초 남짓이라
    /// 왕복을 기다리면 내 화면에서 웅크리는 자세가 거의 안 보인다. (PlayerJump.IsAirborne과 같은 사정)
    /// </summary>
    public bool IsCrouchRequested =>
        IsSpawned && !IsServer && !IsOwner ? m_isCrouchRequestedSynced.Value : m_isCrouchRequested;

    /// <summary>서기(0)↔앉기(1) 블렌딩 진행도. 콜라이더·카메라가 모션과 같은 속도로 따라오도록 공유한다.</summary>
    public float CrouchBlend => m_crouchBlend;

    /// <summary>현재 블렌딩 기준으로 머리가 내려간 높이(m). PlayerMovement가 카메라를 같이 낮추는 데 쓴다.</summary>
    public float HeadDrop => m_standHeight - m_controller.height;

    /// <summary>
    /// HeadDrop이 변하는 속도(m/초). PlayerMovement가 카메라를 <b>같은 속도로</b> 따라오게 하는 데 쓴다 —
    /// 속도를 맞춰야 지상에서 카메라가 콜라이더보다 늦게 내려가는 일이 없다. (#236 취지 유지, #189)
    /// </summary>
    public float HeadDropRate => (m_standHeight - m_crouchHeight) / k_blendDuration;

    /// <summary>앉기 상태가 바뀔 때 발행 — UI·사운드 훅용.</summary>
    public event Action<bool> OnCrouchChanged;

    private void Awake()
    {
        m_controller = GetComponent<CharacterController>();
        m_inputHandler = GetComponent<PlayerInputHandler>();
        m_incapacitation = GetComponent<PlayerIncapacitation>();
        m_jump = GetComponent<PlayerJump>();

        m_standHeight = m_controller.height;
        m_standCenterY = m_controller.center.y;
    }

    public override void OnNetworkSpawn()
    {
        // 원격 피어는 서버 Set 경로를 타지 않으므로, 동기화값 변화를 이벤트로 중계한다.
        m_isCrouchingSynced.OnValueChanged += HandleSyncedChanged;

        // 늦게 접속한 클라: 이미 앉아 있는 플레이어의 콜라이더를 즉시 맞춘다.
        // (OnValueChanged는 '변화' 시에만 발생하므로 스폰 시 한 번 맞춰줘야 한다)
        // 콜라이더 기준은 눌림 여부다 — 공중에서 웅크린 채 접속을 마주해도 크기가 자세와 맞는다.
        m_crouchBlend = IsCrouchRequested ? 1f : 0f;
        ApplyColliderHeight();

        if (IsOwner)
        {
            m_inputHandler.OnCrouchChanged += HandleCrouchInput;
        }
    }

    public override void OnNetworkDespawn()
    {
        m_isCrouchingSynced.OnValueChanged -= HandleSyncedChanged;

        if (IsOwner)
        {
            m_inputHandler.OnCrouchChanged -= HandleCrouchInput;
        }
    }

    // 서버(호스트 포함)는 UpdateServerState에서 이벤트를 직접 발행하므로 여기선 원격 클라만 중계 (이중 발행 방지)
    private void HandleSyncedChanged(bool previous, bool current)
    {
        if (IsServer)
            return;
        OnCrouchChanged?.Invoke(current);
    }

    // 오너 로컬 입력 → 서버에 홀드 여부만 전달. 앉기 상태를 스스로 바꾸지 않는다. (서버 권위)
    private void HandleCrouchInput(bool pressed)
    {
        m_isCrouchRequested = pressed; // 내 화면 자세는 즉시 (IsCrouchRequested 주석 참고)
        RequestCrouchServerRpc(pressed);
    }

    // 눌림 여부는 여기서 곧바로 전파한다 — Update에서 "값이 달라졌을 때만" 쓰면 호스트가 새어 나간다.
    // 호스트는 오너이자 서버라 HandleCrouchInput의 로컬 반영과 이 RPC 본문이 같은 호출에서 처리되고
    // (호스트 ServerRpc는 인라인 실행), 두 필드가 동시에 채워져 발산이 영영 생기지 않기 때문이다.
    // 그러면 남의 화면에서 호스트만 앉기 자세가 안 보인다. (PlayerJump.ReportAirborneServerRpc와 같은 형태)
    [ServerRpc]
    private void RequestCrouchServerRpc(bool pressed)
    {
        m_crouchRequested = pressed;
        m_isCrouchRequested = pressed; // 서버 인스턴스의 자세 값 — 오너가 아닐 때 IsCrouchRequested가 읽는다
        m_isCrouchRequestedSynced.Value = pressed;
    }

    private void Update()
    {
        if (!IsSpawned || IsServer)
        {
            UpdateServerState(); // 서버·오프라인(비네트워크 테스트)에서만 앉기 여부를 판정
        }

        UpdateBlend(); // 콜라이더 높이는 모든 인스턴스에서 동기화값을 따라간다
    }

    // 서버 판정: 오너가 Ctrl을 누르고 있고, 다운도 아니고, 발이 땅에 붙어 있으면 앉는다.
    // 공중에서 보류하는 이유는 두 가지 — 자세와 콜라이더가 따로 노는 것을 막고,
    // 공중에서 콜라이더를 줄여 좁은 틈을 통과하는 크라우치 점프를 막는다. (#189)
    private void UpdateServerState()
    {
        // 눌림 여부(m_isCrouchRequested)는 공중에서도 그대로 전파해야 하지만, 그 갱신은 여기가 아니라
        // RequestCrouchServerRpc가 한다 — 이유는 그쪽 주석 참고.

        bool desired =
            m_crouchRequested
            && !(m_incapacitation != null && m_incapacitation.IsIncapacitated)
            && !(m_jump != null && m_jump.IsAirborne);
        if (m_isCrouching == desired)
            return;

        m_isCrouching = desired;
        if (IsSpawned && IsServer)
            m_isCrouchingSynced.Value = desired;
        OnCrouchChanged?.Invoke(desired);
    }

    // 앉기 블렌딩을 k_blendDuration에 맞춰 진행시키고 콜라이더에 반영한다 — 애니메이터 전환과 같은 시간.
    // 기준이 IsCrouching이 아니라 IsCrouchRequested인 이유: 콜라이더는 '앉기 상태'가 아니라
    // '웅크린 자세'를 따라야 보이는 몸과 충돌 크기가 어긋나지 않는다. 공중에서 앉기 키를 누르면
    // 자세와 함께 콜라이더도 줄어들고, 착지 시점엔 이미 줄어든 상태다. (#189)
    private void UpdateBlend()
    {
        float target = IsCrouchRequested ? 1f : 0f;
        if (Mathf.Approximately(m_crouchBlend, target))
            return;

        m_crouchBlend = Mathf.MoveTowards(m_crouchBlend, target, Time.deltaTime / k_blendDuration);
        ApplyColliderHeight();
    }

    // 발 위치(캡슐 바닥)를 고정한 채 높이만 줄인다 — 중심을 그대로 두면 발이 바닥을 뚫거나 공중에 뜬다.
    private void ApplyColliderHeight()
    {
        float height = Mathf.Lerp(m_standHeight, m_crouchHeight, m_crouchBlend);
        m_controller.height = height;

        Vector3 center = m_controller.center;
        center.y = m_standCenterY - (m_standHeight - height) * 0.5f;
        m_controller.center = center;
    }
}
