using System;
using UnityEngine;
using UnityEngine.InputSystem;
// using Unity.Netcode; // TODO: 네트워크 테스트 시 주석 해제

public class PlayerInputHandler : MonoBehaviour // TODO: 네트워크 테스트 시 NetworkBehaviour로 복구
{
    [Header("Input Actions")]
    [SerializeField] private InputActionReference m_moveAction;
    [SerializeField] private InputActionReference m_lookAction;
    [SerializeField] private InputActionReference m_interactAction;
    [SerializeField] private InputActionReference m_sprintAction;

    public Vector2 MoveInput { get; private set; }
    public Vector2 LookInput { get; private set; }
    public bool IsSprinting { get; private set; }

    public event Action OnInteractStarted;   // 채널링 시작 (버튼 누름)
    public event Action OnInteractPerformed; // Hold 완료 (3초 채움)
    public event Action OnInteractCanceled;  // 중간에 뗌

    private void OnEnable() // TODO: 네트워크 테스트 시 OnNetworkSpawn으로 복구
    {
        // if (!IsOwner)
        // {
        //     enabled = false;
        //     return;
        // }

        m_moveAction.action.Enable();
        m_lookAction.action.Enable();
        m_interactAction.action.Enable();
        m_sprintAction.action.Enable();

        m_moveAction.action.performed += OnMove;
        m_moveAction.action.canceled += OnMove;
        m_lookAction.action.performed += OnLook;
        m_lookAction.action.canceled += OnLook;
        m_interactAction.action.started += OnInteractStartedHandler;
        m_interactAction.action.performed += OnInteractPerformedHandler;
        m_interactAction.action.canceled += OnInteractCanceledHandler;
        m_sprintAction.action.performed += OnSprintPerformed;
        m_sprintAction.action.canceled += OnSprintCanceled;
    }

    private void OnDisable() // TODO: 네트워크 테스트 시 OnNetworkDespawn으로 복구
    {
        // if (!IsOwner) return;

        m_moveAction.action.performed -= OnMove;
        m_moveAction.action.canceled -= OnMove;
        m_lookAction.action.performed -= OnLook;
        m_lookAction.action.canceled -= OnLook;
        m_interactAction.action.started -= OnInteractStartedHandler;
        m_interactAction.action.performed -= OnInteractPerformedHandler;
        m_interactAction.action.canceled -= OnInteractCanceledHandler;
        m_sprintAction.action.performed -= OnSprintPerformed;
        m_sprintAction.action.canceled -= OnSprintCanceled;

        m_moveAction.action.Disable();
        m_lookAction.action.Disable();
        m_interactAction.action.Disable();
        m_sprintAction.action.Disable();
    }

    private void OnMove(InputAction.CallbackContext ctx) => MoveInput = ctx.ReadValue<Vector2>();
    private void OnLook(InputAction.CallbackContext ctx) => LookInput = ctx.ReadValue<Vector2>();

    private void OnInteractStartedHandler(InputAction.CallbackContext ctx) => OnInteractStarted?.Invoke();
    private void OnInteractPerformedHandler(InputAction.CallbackContext ctx) => OnInteractPerformed?.Invoke();
    private void OnInteractCanceledHandler(InputAction.CallbackContext ctx) => OnInteractCanceled?.Invoke();

    private void OnSprintPerformed(InputAction.CallbackContext ctx) => IsSprinting = true;
    private void OnSprintCanceled(InputAction.CallbackContext ctx) => IsSprinting = false;
}
