using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(CharacterController))]
[RequireComponent(typeof(PlayerInputHandler))]
public class PlayerMovement : NetworkBehaviour
{
    [Header("이동")]
    [SerializeField]
    private float m_moveSpeed = 5f;

    [SerializeField]
    private float m_sprintSpeed = 8f;

    [SerializeField]
    private float m_gravity = -9.81f;

    // PlayerAnimationDriver가 속도 정규화에 사용 (실제 속도 ↔ 블렌드 트리 좌표 분리)
    public float MoveSpeed => m_moveSpeed;
    public float SprintSpeed => m_sprintSpeed;

    [Header("1인칭 시점")]
    [SerializeField]
    private Camera playerCamera;

    [SerializeField]
    private float m_mouseSensitivity = 1f;

    [SerializeField]
    private float m_minPitch = -80f;

    [SerializeField]
    private float m_maxPitch = 80f;

    [SerializeField]
    private Transform m_ownBodyRoot; // 내 카메라에서만 안 보이게 할 캐릭터 몸(머리) 루트

    private CharacterController m_controller;
    private PlayerInputHandler m_inputHandler;
    private float m_pitch;
    private float m_verticalVelocity;
    private bool m_cursorUnlocked; // 임시: OnGUI 버튼 조작용 커서 해제 상태

    public override void OnNetworkSpawn()
    {
        m_controller = GetComponent<CharacterController>();
        m_inputHandler = GetComponent<PlayerInputHandler>();

        if (!IsOwner)
        {
            playerCamera.gameObject.SetActive(false);
            enabled = false;
            return;
        }

        if (m_ownBodyRoot != null)
        {
            SetLayerRecursively(m_ownBodyRoot, LayerMask.NameToLayer("OwnBody")); // 내 카메라에서만 안 보이게
        }

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    private void SetLayerRecursively(Transform root, int layer)
    {
        root.gameObject.layer = layer;
        foreach (Transform child in root)
        {
            SetLayerRecursively(child, layer);
        }
    }

    private void Update()
    {
        // 임시: ESC로 커서 잠금/해제 토글 — NetworkBootstrap의 OnGUI 버튼 조작용.
        // lockState를 명시적으로 None으로 바꿔야 클릭 시 엔진이 재잠금하지 않는다.
        // 정식 UI(메뉴/로비)가 들어오면 그쪽 시스템으로 옮기고 이 블록은 제거할 것.
        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            m_cursorUnlocked = !m_cursorUnlocked;
            Cursor.lockState = m_cursorUnlocked ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = m_cursorUnlocked;
        }

        if (!m_cursorUnlocked)
        {
            HandleLook(); // 커서 해제 중에는 시점 회전 정지 (마우스 이동이 화면을 돌리지 않게)
        }

        HandleMove();
    }

    private void HandleLook()
    {
        Vector2 look = m_inputHandler.LookInput * m_mouseSensitivity;

        transform.Rotate(Vector3.up * look.x);

        m_pitch = Mathf.Clamp(m_pitch - look.y, m_minPitch, m_maxPitch);
        playerCamera.transform.localEulerAngles = new Vector3(m_pitch, 0f, 0f);
    }

    private void HandleMove()
    {
        Vector2 input = m_inputHandler.MoveInput;
        Vector3 moveDirection = (
            transform.right * input.x + transform.forward * input.y
        ).normalized;

        if (m_controller.isGrounded && m_verticalVelocity < 0f)
        {
            m_verticalVelocity = -2f;
        }
        m_verticalVelocity += m_gravity * Time.deltaTime;

        float speed = m_inputHandler.IsSprinting ? m_sprintSpeed : m_moveSpeed;
        Vector3 velocity = moveDirection * speed + Vector3.up * m_verticalVelocity;
        m_controller.Move(velocity * Time.deltaTime);
    }
}
