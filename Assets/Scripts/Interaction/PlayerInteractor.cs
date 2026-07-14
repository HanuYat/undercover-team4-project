using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(PlayerInputHandler))]
public class PlayerInteractor : NetworkBehaviour
{
    [Header("레이캐스트")]
    [SerializeField] private Camera m_camera;
    [SerializeField] private float m_range = 3f;
    [SerializeField] private LayerMask m_interactMask = ~0;

    public IInteractable CurrentInteractable { get; private set; }
    public GameObject CurrentTarget { get; private set; } // 아이템 타겟팅/UI용

    private PlayerInputHandler m_inputHandler;
    private PlayerIncapacitation m_incapacitation; // 다운(무력화) 중 상호작용 차단용 (#105)

    public override void OnNetworkSpawn()
    {
        m_inputHandler = GetComponent<PlayerInputHandler>();
        m_incapacitation = GetComponent<PlayerIncapacitation>();
        if (m_camera == null) m_camera = Camera.main;

        if (!IsOwner)
        {
            enabled = false;
            return;
        }

        m_inputHandler.OnInteractPerformed += HandleInteract;
    }

    public override void OnNetworkDespawn()
    {
        if (!IsOwner) return;

        m_inputHandler.OnInteractPerformed -= HandleInteract;
    }

    private void Update()
    {
        UpdateTarget();
    }

    private void UpdateTarget()
    {
        if (m_camera == null) return;

        Ray ray = new Ray(m_camera.transform.position, m_camera.transform.forward);
        if (Physics.Raycast(ray, out RaycastHit hit, m_range, m_interactMask))
        {
            CurrentTarget = hit.collider.gameObject;
            CurrentInteractable = hit.collider.GetComponentInParent<IInteractable>();
        }
        else
        {
            CurrentTarget = null;
            CurrentInteractable = null;
        }
    }

    private void HandleInteract()
    {
        // 다운(무력화) 중에는 상호작용 발동 불가 (#105)
        if (m_incapacitation != null && m_incapacitation.IsIncapacitated) return;

        CurrentInteractable?.Interact(gameObject);
    }
}
