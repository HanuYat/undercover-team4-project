using TMPro;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

public class PlayerNameTag : NetworkBehaviour
{
    [SerializeField]
    private TextMeshProUGUI m_label;

    [SerializeField]
    private Transform m_tagRoot;

    [SerializeField]
    private float m_showDistance = 15f;
    private Transform m_cam;

    private readonly NetworkVariable<FixedString64Bytes> m_name = new(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Owner
    );

    [SerializeField]
    private GameObject m_speakerIcon;

    /// <summary>동기화된 표시 이름 — 정산 코믹 스탯 등이 clientId→이름 변환에 읽는다. (#107)</summary>
    public string DisplayName => m_name.Value.ToString();

    private readonly NetworkVariable<FixedString64Bytes> m_playerId = new(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Owner
    );

    private void LateUpdate()
    {
        if (IsOwner || !IsSpawned)
            return;

        // 이름이 아직 동기화되지 않았으면 빈 라벨이 보이지 않도록 숨긴다
        if (m_name.Value.IsEmpty)
        {
            m_tagRoot.gameObject.SetActive(false);
            return;
        }

        var cam = ResolveCamera();
        if (cam == null)
            return;

        m_tagRoot.rotation = cam.rotation;
        m_tagRoot.gameObject.SetActive(
            Vector3.Distance(cam.position, transform.position) <= m_showDistance
        );
    }

    public override void OnNetworkSpawn()
    {
        if (IsOwner)
        {
            var fs = new FixedString64Bytes();
            fs.CopyFromTruncated(App.Net.Auth.Nickname ?? string.Empty);
            m_name.Value = fs;

            var pid = new FixedString64Bytes();
            pid.CopyFromTruncated(App.Net.Auth.PlayerId ?? string.Empty);
            m_playerId.Value = pid;

            m_tagRoot.gameObject.SetActive(false);

            if (m_speakerIcon != null)
                m_speakerIcon.SetActive(false);
        }
        else
        {
            m_name.OnValueChanged += HandleNameChanged;
            if (!m_name.Value.IsEmpty)
                HandleNameChanged(default, m_name.Value);

            if (App.Net.Vivox != null)
                App.Net.Vivox.OnSpeakingChanged += HandleSpeakingChanged;
            m_playerId.OnValueChanged += HandleIdChanged;
            RefreshSpeakerIcon();
        }
    }

    public override void OnNetworkDespawn()
    {
        m_name.OnValueChanged -= HandleNameChanged;
        m_playerId.OnValueChanged -= HandleIdChanged;
        if (App.Net.Vivox != null)
            App.Net.Vivox.OnSpeakingChanged -= HandleSpeakingChanged;
    }

    private void HandleNameChanged(FixedString64Bytes previous, FixedString64Bytes current)
    {
        m_label.text = current.ToString();
    }

    private void HandleIdChanged(FixedString64Bytes previous, FixedString64Bytes current) =>
        RefreshSpeakerIcon();

    private void HandleSpeakingChanged(string playerId, bool speaking)
    {
        if (playerId == m_playerId.Value.ToString())
            SetSpeakerIcon(speaking);
    }

    private void RefreshSpeakerIcon()
    {
        string pid = m_playerId.Value.ToString();
        bool speaking =
            !string.IsNullOrEmpty(pid) && App.Net.Vivox != null && App.Net.Vivox.IsSpeaking(pid);
        SetSpeakerIcon(speaking);
    }

    private void SetSpeakerIcon(bool on)
    {
        if (m_speakerIcon != null)
            m_speakerIcon.SetActive(on);
    }

    private Transform ResolveCamera()
    {
        if (m_cam != null)
            return m_cam;

        var nm = NetworkManager.Singleton;
        if (nm != null && nm.LocalClient != null && nm.LocalClient.PlayerObject != null)
        {
            var cam = nm.LocalClient.PlayerObject.GetComponentInChildren<Camera>();
            if (cam != null)
                m_cam = cam.transform;
        }

        if (m_cam == null && Camera.main != null)
            m_cam = Camera.main.transform;

        return m_cam;
    }
}
