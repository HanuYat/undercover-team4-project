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

    private CharacterController m_controller;
    private PlayerInputHandler m_inputHandler;
    private PlayerIncapacitation m_incapacitation; // 다운 중엔 앉기 해제 (콜라이더가 줄어든 채 고착되는 것 방지)
    private float m_standHeight; // 서기 CharacterController 높이 — 프리팹 초기값에서 캡처
    private float m_standCenterY; // 서기 CharacterController 중심 y — 발 위치 고정 계산 기준
    private float m_crouchBlend; // 0 = 완전히 섬, 1 = 완전히 앉음

    /// <summary>앉기 여부. 서버·오프라인은 실참조, 원격 피어는 동기화값으로 판정. (PlayerIncapacitation.IsIncapacitated 관례)</summary>
    public bool IsCrouching => IsSpawned && !IsServer ? m_isCrouchingSynced.Value : m_isCrouching;

    /// <summary>서기(0)↔앉기(1) 블렌딩 진행도. 콜라이더·카메라가 모션과 같은 속도로 따라오도록 공유한다.</summary>
    public float CrouchBlend => m_crouchBlend;

    /// <summary>현재 블렌딩 기준으로 머리가 내려간 높이(m). PlayerMovement가 카메라를 같이 낮추는 데 쓴다.</summary>
    public float HeadDrop => m_standHeight - m_controller.height;

    /// <summary>앉기 상태가 바뀔 때 발행 — UI·사운드 훅용.</summary>
    public event Action<bool> OnCrouchChanged;

    private void Awake()
    {
        m_controller = GetComponent<CharacterController>();
        m_inputHandler = GetComponent<PlayerInputHandler>();
        m_incapacitation = GetComponent<PlayerIncapacitation>();

        m_standHeight = m_controller.height;
        m_standCenterY = m_controller.center.y;
    }

    public override void OnNetworkSpawn()
    {
        // 원격 피어는 서버 Set 경로를 타지 않으므로, 동기화값 변화를 이벤트로 중계한다.
        m_isCrouchingSynced.OnValueChanged += HandleSyncedChanged;

        // 늦게 접속한 클라: 이미 앉아 있는 플레이어의 콜라이더를 즉시 맞춘다.
        // (OnValueChanged는 '변화' 시에만 발생하므로 스폰 시 한 번 맞춰줘야 한다)
        m_crouchBlend = IsCrouching ? 1f : 0f;
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
        RequestCrouchServerRpc(pressed);
    }

    [ServerRpc]
    private void RequestCrouchServerRpc(bool pressed)
    {
        m_crouchRequested = pressed;
    }

    private void Update()
    {
        if (!IsSpawned || IsServer)
        {
            UpdateServerState(); // 서버·오프라인(비네트워크 테스트)에서만 앉기 여부를 판정
        }

        UpdateBlend(); // 콜라이더 높이는 모든 인스턴스에서 동기화값을 따라간다
    }

    // 서버 판정: 오너가 Ctrl을 누르고 있고 다운 상태가 아니면 앉는다.
    private void UpdateServerState()
    {
        bool desired =
            m_crouchRequested && !(m_incapacitation != null && m_incapacitation.IsIncapacitated);
        if (m_isCrouching == desired)
            return;

        m_isCrouching = desired;
        if (IsSpawned && IsServer)
            m_isCrouchingSynced.Value = desired;
        OnCrouchChanged?.Invoke(desired);
    }

    // 앉기 블렌딩을 k_blendDuration에 맞춰 진행시키고 콜라이더에 반영한다 — 애니메이터 전환과 같은 시간.
    private void UpdateBlend()
    {
        float target = IsCrouching ? 1f : 0f;
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
