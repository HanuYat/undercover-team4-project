using UnityEngine;

[RequireComponent(typeof(PlayerInputHandler))]
public class PlayerInteractor : MonoBehaviour
{
    [Header("레이캐스트")]
    [SerializeField] private Camera m_camera;
    [SerializeField] private float m_range = 3f;
    [SerializeField] private LayerMask m_interactMask = ~0;

    public IInteractable CurrentInteractable { get; private set; }
    public GameObject CurrentTarget { get; private set; } // 아이템 타겟팅/UI용

    private PlayerInputHandler m_inputHandler;

    private void Awake()
    {
        m_inputHandler = GetComponent<PlayerInputHandler>();
        if (m_camera == null) m_camera = Camera.main;
    }

    private void OnEnable()  => m_inputHandler.OnInteractPerformed += HandleInteract;
    private void OnDisable() => m_inputHandler.OnInteractPerformed -= HandleInteract;

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
        CurrentInteractable?.Interact(gameObject);
    }
}
