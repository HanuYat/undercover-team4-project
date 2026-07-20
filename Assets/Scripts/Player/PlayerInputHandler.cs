using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerInputHandler : NetworkBehaviour
{
    [Header("Input Actions")]
    [SerializeField]
    private InputActionReference m_moveAction;

    [SerializeField]
    private InputActionReference m_lookAction;

    [SerializeField]
    private InputActionReference m_interactAction;

    [SerializeField]
    private InputActionReference m_sprintAction;

    [SerializeField]
    private InputActionReference m_useItemAction;

    [SerializeField]
    private InputActionReference m_previousAction;

    [SerializeField]
    private InputActionReference m_nextAction;

    [SerializeField]
    private InputActionReference m_dropAction;

    [SerializeField]
    private InputActionReference m_selectSlotAction;

    [SerializeField]
    private InputActionReference m_toggleInventoryAction;

    [SerializeField]
    private InputActionReference m_crouchAction;

    public Vector2 MoveInput { get; private set; }
    public Vector2 LookInput { get; private set; }
    public bool IsSprinting { get; private set; }

    public event Action OnInteractStarted; // 상호작용 버튼 누름
    public event Action OnInteractPerformed; // 상호작용 발동 — 순수 Button이라 누르는 즉시 발화 (즉시발동)
    public event Action OnInteractCanceled; // 상호작용 버튼 뗌
    public event Action OnUseItemStarted; // 아이템 사용 시작 (좌클릭 누름 — 채널링 시작, #91)
    public event Action OnUseItemCanceled; // 아이템 사용 중단 (좌클릭 뗌 — 채널링 취소, #91)
    public event Action OnPreviousItem; // 마우스 휠 위 — 이전 아이템으로 전환 (#46)
    public event Action OnNextItem; // 마우스 휠 아래 — 다음 아이템으로 전환 (#46)
    public event Action OnDropItem; // 장착 아이템 버리기 (#88)
    public event Action<int> OnSelectSlot; // 숫자키 1~3 — 슬롯 직접 선택, 인덱스 0~2 (#144)
    public event Action OnToggleInventory; // Tab — 인벤토리 편집 모드 토글 (#144)
    public event Action<bool> OnCrouchChanged; // Left Ctrl 홀드 — 누르면 true, 떼면 false (#236)

    private bool m_isSuspended;

    /// <summary>
    /// 게임플레이 입력이 정지된 상태인지 — 텍스트 입력 UI(신호 해석기 #108) 등이 켠다.
    /// 정지 중에는 이동·시점·아이템·상호작용 입력이 전부 끊긴다.
    /// </summary>
    public bool IsSuspended => m_isSuspended;

    /// <summary>
    /// 게임플레이 입력을 일시 정지/재개한다. 타이핑 중 WASD가 이동으로 새는 것을 막는 용도다.
    /// (인벤토리 편집 모드는 "이동 입력이 들어오면 닫기" 방식이라 타이핑에는 쓸 수 없다 — InventoryBarView.Update)
    /// 오너에서만 의미가 있다. 정지/재개는 구독을 건드리지 않고 액션만 켜고 끈다.
    /// </summary>
    public void SetSuspended(bool suspended)
    {
        if (!IsOwner || m_isSuspended == suspended)
            return;

        m_isSuspended = suspended;
        SetActionsEnabled(!suspended);

        if (suspended)
        {
            // 액션을 끄면 진행 중이던 입력의 canceled 콜백이 돌아 캐시값이 비워지지만, 순서에 기대지 않고
            // 여기서 확실히 비운다 — 남아 있으면 정지 중에도 마지막 입력값으로 계속 이동한다.
            MoveInput = Vector2.zero;
            LookInput = Vector2.zero;
            IsSprinting = false;
        }
    }

    // 11개 액션을 한꺼번에 켜고 끈다 — 스폰/디스폰/정지가 같은 목록을 쓰도록 한 곳에 모은다.
    private void SetActionsEnabled(bool value)
    {
        InputActionReference[] actions =
        {
            m_moveAction,
            m_lookAction,
            m_interactAction,
            m_sprintAction,
            m_useItemAction,
            m_previousAction,
            m_nextAction,
            m_dropAction,
            m_selectSlotAction,
            m_toggleInventoryAction,
            m_crouchAction,
        };

        foreach (InputActionReference reference in actions)
        {
            if (reference == null || reference.action == null)
                continue;

            if (value)
                reference.action.Enable();
            else
                reference.action.Disable();
        }
    }

    public override void OnNetworkSpawn()
    {
        if (!IsOwner)
        {
            enabled = false;
            return;
        }

        SetActionsEnabled(true);

        m_moveAction.action.performed += OnMove;
        m_moveAction.action.canceled += OnMove;
        m_lookAction.action.performed += OnLook;
        m_lookAction.action.canceled += OnLook;
        m_interactAction.action.started += OnInteractStartedHandler;
        m_interactAction.action.performed += OnInteractPerformedHandler;
        m_interactAction.action.canceled += OnInteractCanceledHandler;
        m_sprintAction.action.performed += OnSprintPerformed;
        m_sprintAction.action.canceled += OnSprintCanceled;
        m_useItemAction.action.started += OnUseItemStartedHandler;
        m_useItemAction.action.canceled += OnUseItemCanceledHandler;
        m_previousAction.action.performed += OnPreviousItemHandler;
        m_nextAction.action.performed += OnNextItemHandler;
        m_dropAction.action.performed += OnDropItemHandler;
        m_selectSlotAction.action.performed += OnSelectSlotHandler;
        m_toggleInventoryAction.action.performed += OnToggleInventoryHandler;
        m_crouchAction.action.started += OnCrouchStartedHandler;
        m_crouchAction.action.canceled += OnCrouchCanceledHandler;
    }

    public override void OnNetworkDespawn()
    {
        if (!IsOwner)
            return;

        m_moveAction.action.performed -= OnMove;
        m_moveAction.action.canceled -= OnMove;
        m_lookAction.action.performed -= OnLook;
        m_lookAction.action.canceled -= OnLook;
        m_interactAction.action.started -= OnInteractStartedHandler;
        m_interactAction.action.performed -= OnInteractPerformedHandler;
        m_interactAction.action.canceled -= OnInteractCanceledHandler;
        m_sprintAction.action.performed -= OnSprintPerformed;
        m_sprintAction.action.canceled -= OnSprintCanceled;
        m_useItemAction.action.started -= OnUseItemStartedHandler;
        m_useItemAction.action.canceled -= OnUseItemCanceledHandler;
        m_previousAction.action.performed -= OnPreviousItemHandler;
        m_nextAction.action.performed -= OnNextItemHandler;
        m_dropAction.action.performed -= OnDropItemHandler;
        m_selectSlotAction.action.performed -= OnSelectSlotHandler;
        m_toggleInventoryAction.action.performed -= OnToggleInventoryHandler;
        m_crouchAction.action.started -= OnCrouchStartedHandler;
        m_crouchAction.action.canceled -= OnCrouchCanceledHandler;

        SetActionsEnabled(false);
        m_isSuspended = false; // 재접속·재스폰 시 정지 상태가 남지 않도록 초기화
    }

    private void OnMove(InputAction.CallbackContext ctx) => MoveInput = ctx.ReadValue<Vector2>();

    private void OnLook(InputAction.CallbackContext ctx) => LookInput = ctx.ReadValue<Vector2>();

    private void OnInteractStartedHandler(InputAction.CallbackContext ctx) =>
        OnInteractStarted?.Invoke();

    private void OnInteractPerformedHandler(InputAction.CallbackContext ctx) =>
        OnInteractPerformed?.Invoke();

    private void OnInteractCanceledHandler(InputAction.CallbackContext ctx) =>
        OnInteractCanceled?.Invoke();

    private void OnSprintPerformed(InputAction.CallbackContext ctx) => IsSprinting = true;

    private void OnSprintCanceled(InputAction.CallbackContext ctx) => IsSprinting = false;

    private void OnUseItemStartedHandler(InputAction.CallbackContext ctx) =>
        OnUseItemStarted?.Invoke();

    private void OnUseItemCanceledHandler(InputAction.CallbackContext ctx) =>
        OnUseItemCanceled?.Invoke();

    private void OnPreviousItemHandler(InputAction.CallbackContext ctx) => OnPreviousItem?.Invoke();

    private void OnNextItemHandler(InputAction.CallbackContext ctx) => OnNextItem?.Invoke();

    private void OnDropItemHandler(InputAction.CallbackContext ctx) => OnDropItem?.Invoke();

    // 숫자키 1~3 바인딩이 한 액션에 묶여 있어, 눌린 키 이름("1"~"3")으로 슬롯 인덱스(0~2)를 구한다.
    private void OnSelectSlotHandler(InputAction.CallbackContext ctx)
    {
        if (int.TryParse(ctx.control.name, out int keyNumber))
        {
            OnSelectSlot?.Invoke(keyNumber - 1);
        }
    }

    private void OnToggleInventoryHandler(InputAction.CallbackContext ctx) =>
        OnToggleInventory?.Invoke();

    private void OnCrouchStartedHandler(InputAction.CallbackContext ctx) =>
        OnCrouchChanged?.Invoke(true);

    private void OnCrouchCanceledHandler(InputAction.CallbackContext ctx) =>
        OnCrouchChanged?.Invoke(false);
}
