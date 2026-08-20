using TMPro;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 플레이어 머리 위 이름표 — 닉네임 / 발화 아이콘 / 음소거 아이콘(#430).
///
/// 닉네임·PlayerId·음소거는 <b>오너 쓰기 NetworkVariable</b>로 올린다(서버가 모르는 로컬 값).
/// 발화는 동기화하지 않는다 — 각 클라의 Vivox가 자기에게 들리는 참가자만 알려주므로 로컬로 그린다.
///
/// 음소거는 로비 로스터(<see cref="LobbyPlayerEntry"/>)에도 같은 값이 실린다. 중복 경로가 아니라
/// <b>씬마다 존재하는 유일한 운반 수단</b>이다 — 로비에는 플레이어 오브젝트가 없고(#214),
/// 게임 씬에는 로비 로스터가 언로드돼 없다. 값의 출처는 양쪽 모두 GameSettings.MicMuted 하나다.
/// </summary>
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

    [SerializeField]
    private GameObject m_micMutedIcon;

    // 발화 아이콘은 음소거와 배타다 — 소리가 나가지 않는 사람에게 발화 표시가 켜지면 안 된다.
    // 두 신호가 서로 다른 경로로 도착하므로(음소거는 NetworkVariable, 발화는 로컬 Vivox 이벤트)
    // 마지막 발화 값을 기억해 두고 둘 중 어느 쪽이 바뀌어도 같은 판정을 다시 내린다. (#430)
    private bool m_speaking;

    /// <summary>동기화된 표시 이름 — 정산 코믹 스탯 등이 clientId→이름 변환에 읽는다. (#107)</summary>
    public string DisplayName => m_name.Value.ToString();

    private readonly NetworkVariable<FixedString64Bytes> m_playerId = new(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Owner
    );

    // 자기 음소거는 로컬 입력 장치 설정이라 Vivox 참가자 API로 남의 상태를 조회할 수 없다 —
    // 발화(로컬 이벤트로 충분)와 달리 오너가 명시적으로 올려야 한다. (#430)
    private readonly NetworkVariable<bool> m_micMuted = new(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Owner
    );

    // <b>"내 캐릭터인가"는 스폰 시점에 굳힌다 — <c>IsOwner</c>로 매번 묻지 않는다.</b>
    // 사망 중에는 소유권이 서버로 넘어가므로(#763 A-1) <c>IsOwner</c>의 뜻이 라운드 중에 뒤집힌다:
    // 호스트에서 남의 시체가 "내 것"이 되고, 죽은 클라에서 자기 몸이 "남의 것"이 된다.
    // 화면·HUD는 <b>누가 조작하는 몸인가</b>를 물어야 하고 그 답은 스폰 때 정해진다.
    private bool m_isLocalPlayer;

    private void LateUpdate()
    {
        if (m_isLocalPlayer || !IsSpawned)
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
        m_isLocalPlayer = IsOwner; // 라운드 중에 뒤집히는 값이라 여기서 굳힌다 (#763 A-1)

        if (IsOwner)
        {
            m_name.Value = App.Net.Auth.Nickname.ToFixed64();
            m_playerId.Value = App.Net.Auth.PlayerId.ToFixed64();

            // 씬을 넘어오며 플레이어가 다시 스폰되므로 매번 현재 설정값에서 시작한다 (#430)
            m_micMuted.Value = GameSettings.MicMuted;
            GameSettings.OnMicMutedChanged += HandleOwnerMicMutedChanged;

            m_tagRoot.gameObject.SetActive(false);

            if (m_speakerIcon != null)
                m_speakerIcon.SetActive(false);

            // 내 음소거는 HUD(MicStatusHud)가 보여준다 — 이름표는 남에게 보이는 표시다
            if (m_micMutedIcon != null)
                m_micMutedIcon.SetActive(false);
        }
        else
        {
            m_name.OnValueChanged += HandleNameChanged;
            if (!m_name.Value.IsEmpty)
                HandleNameChanged(default, m_name.Value);

            if (App.Net.Vivox != null)
                App.Net.Vivox.OnSpeakingChanged += HandleSpeakingChanged;
            m_playerId.OnValueChanged += HandleIdChanged;
            m_micMuted.OnValueChanged += HandleMicMutedChanged;

            // 음소거를 먼저 그린다 — 발화 아이콘 판정이 음소거 값을 읽는다 (배타)
            RefreshMicMutedIcon();
            RefreshSpeakerIcon();
        }
    }

    public override void OnNetworkDespawn()
    {
        m_name.OnValueChanged -= HandleNameChanged;
        m_playerId.OnValueChanged -= HandleIdChanged;
        m_micMuted.OnValueChanged -= HandleMicMutedChanged;
        if (App.Net.Vivox != null)
            App.Net.Vivox.OnSpeakingChanged -= HandleSpeakingChanged;

        // 오너만 구독했지만 무조건 뗀다 — 구독하지 않았으면 no-op이고, 씬 전환마다 플레이어가
        // 다시 스폰되므로 여기서 빠지면 죽은 이름표를 가리키는 static 구독이 쌓인다. (#430)
        GameSettings.OnMicMutedChanged -= HandleOwnerMicMutedChanged;
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
        m_speaking = on;

        if (m_speakerIcon != null)
            m_speakerIcon.SetActive(on && !m_micMuted.Value);
    }

    // 오너가 자기 음소거를 올린다 — 값의 출처는 GameSettings 하나이고 여기서는 나르기만 한다 (#430)
    private void HandleOwnerMicMutedChanged(bool muted)
    {
        if (IsOwner && IsSpawned)
            m_micMuted.Value = muted;
    }

    private void HandleMicMutedChanged(bool previous, bool current) => RefreshMicMutedIcon();

    private void RefreshMicMutedIcon()
    {
        if (m_micMutedIcon != null)
            m_micMutedIcon.SetActive(m_micMuted.Value);

        SetSpeakerIcon(m_speaking); // 음소거로 바뀌었으면 켜져 있던 발화 아이콘을 내린다
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
