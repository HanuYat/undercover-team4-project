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
    private InputActionReference m_attackAction;

    [SerializeField]
    private InputActionReference m_previousAction;

    [SerializeField]
    private InputActionReference m_nextAction;

    public Vector2 MoveInput { get; private set; }
    public Vector2 LookInput { get; private set; }
    public bool IsSprinting { get; private set; }

    public event Action OnInteractStarted; // 채널링 시작 (버튼 누름)
    public event Action OnInteractPerformed; // Hold 완료 (3초 채움)
    public event Action OnInteractCanceled; // 중간에 뗌
    public event Action OnAttackPerformed; // 아이템 사용 (조준 대상에 사용)
    public event Action OnPreviousItem; // 마우스 휠 위 — 이전 아이템으로 전환 (#46)
    public event Action OnNextItem; // 마우스 휠 아래 — 다음 아이템으로 전환 (#46)

    public override void OnNetworkSpawn()
    {
        if (!IsOwner)
        {
            enabled = false;
            return;
        }

        m_moveAction.action.Enable();
        m_lookAction.action.Enable();
        m_interactAction.action.Enable();
        m_sprintAction.action.Enable();
        m_attackAction.action.Enable();
        m_previousAction.action.Enable();
        m_nextAction.action.Enable();

        m_moveAction.action.performed += OnMove;
        m_moveAction.action.canceled += OnMove;
        m_lookAction.action.performed += OnLook;
        m_lookAction.action.canceled += OnLook;
        m_interactAction.action.started += OnInteractStartedHandler;
        m_interactAction.action.performed += OnInteractPerformedHandler;
        m_interactAction.action.canceled += OnInteractCanceledHandler;
        m_sprintAction.action.performed += OnSprintPerformed;
        m_sprintAction.action.canceled += OnSprintCanceled;
        m_attackAction.action.performed += OnAttackPerformedHandler;
        m_previousAction.action.performed += OnPreviousItemHandler;
        m_nextAction.action.performed += OnNextItemHandler;
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
        m_attackAction.action.performed -= OnAttackPerformedHandler;
        m_previousAction.action.performed -= OnPreviousItemHandler;
        m_nextAction.action.performed -= OnNextItemHandler;

        m_moveAction.action.Disable();
        m_lookAction.action.Disable();
        m_interactAction.action.Disable();
        m_sprintAction.action.Disable();
        m_attackAction.action.Disable();
        m_previousAction.action.Disable();
        m_nextAction.action.Disable();
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

    private void OnAttackPerformedHandler(InputAction.CallbackContext ctx) =>
        OnAttackPerformed?.Invoke();

    private void OnPreviousItemHandler(InputAction.CallbackContext ctx) => OnPreviousItem?.Invoke();

    private void OnNextItemHandler(InputAction.CallbackContext ctx) => OnNextItem?.Invoke();
}
