using TMPro;
using Unity.Collections;
using Unity.Netcode;
using Unity.Services.Authentication;
using UnityEngine;

public class PlayerNameTag : NetworkBehaviour
{
    [SerializeField] private TextMeshProUGUI m_label;
    [SerializeField] private Transform m_tagRoot;
    [SerializeField] private float m_showDistance = 15f;
    private Transform m_cam;

    private readonly NetworkVariable<FixedString64Bytes> m_name = new(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Owner);

    private void LateUpdate()
    {
        if (IsOwner) return;

        var cam = ResolveCamera();
        if (cam == null) return;

        m_tagRoot.rotation = cam.rotation;
        m_tagRoot.gameObject.SetActive(Vector3.Distance(m_cam.position, transform.position) <= m_showDistance);
    }

    public override void OnNetworkSpawn()
    {
        if (IsOwner)
        {
            var fs = new FixedString64Bytes();
            fs.CopyFromTruncated(AuthenticationService.Instance.PlayerName ?? string.Empty);
            m_name.Value = fs;

            m_tagRoot.gameObject.SetActive(false);
        }
        else
        {
            m_name.OnValueChanged += HandleNameChanged;
            if (!m_name.Value.IsEmpty)
                HandleNameChanged(default, m_name.Value);
        }
    }

    public override void OnNetworkDespawn()
    {
        m_name.OnValueChanged -= HandleNameChanged;
    }

    private void HandleNameChanged(FixedString64Bytes previous, FixedString64Bytes current)
    {
        m_label.text = current.ToString();
    }

    private Transform ResolveCamera()
    {
        if (m_cam != null) return m_cam;

        var nm = NetworkManager.Singleton;
        if (nm != null && nm.LocalClient != null && nm.LocalClient.PlayerObject != null)
        {
            var cam = nm.LocalClient.PlayerObject.GetComponentInChildren<Camera>();
            if (cam != null) m_cam = cam.transform;
        }

        if (m_cam == null && Camera.main != null)
            m_cam = Camera.main.transform;

        return m_cam;
    }
}
