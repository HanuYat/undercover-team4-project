using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 플레이어 로봇 색 (#432) — 순수 코스메틱. <b>서버가 스폰 시점에 심고</b> 전 피어가 각자 칠한다.
///
/// <b>오너 쓰기였다가 서버 쓰기로 바꿨다</b> (#790). 오너는 스폰 메시지를 받은 뒤에야 쓸 수 있어
/// 스폰 페이로드에 색을 실을 방법이 원리적으로 없었고, 남의 로봇이 기본색으로 한 번 칠해진 뒤
/// 왕복 지연만큼 늦게 제 색으로 바뀌었다. 서버는 명부(<see cref="SessionRoster"/>)로 모두의 색을
/// 이미 알고 있으므로 첫 프레임부터 옳게 칠할 수 있다.
///
/// 칠하기는 <see cref="BodyTint"/>에 맡긴다 — 직접 블록을 들면 감전 발광(#477)과 서로 덮어쓴다.
/// 부위 색은 서브메시 순서(<see cref="EBodyPart"/>)로 들어가고, 1인칭 팔처럼 나뉘지 않은
/// 렌더러는 상체 색을 받는다.
/// </summary>
[RequireComponent(typeof(BodyTint))]
public class PlayerCosmetics : NetworkBehaviour
{
    [Tooltip("색 팔레트 — 로비 팔레트와 반드시 같은 에셋을 물릴 것")]
    [SerializeField] private PlayerColorPalette m_palette;

    private readonly NetworkVariable<PlayerColorSet> m_colors = new(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    // 위 값이 <b>진짜 이 사람의 색인지</b> — 늦게 접속하면 서버가 스폰 시점에 색을 모른다(연결 승인에서
    // 스폰되므로 클라가 아직 아무것도 보고하지 못한 상태다). 그때 기본색을 칠해 버리면 오너 보고가
    // 닿는 순간 색이 튀므로, 배정될 때까지 <b>남의 몸을 그리지 않는다</b>. (#790)
    // 별도 값으로 두는 이유: PlayerColorSet은 byte 인덱스라 "미배정"을 표현할 수 없다(0이 곧 첫 색이다).
    private readonly NetworkVariable<bool> m_assigned = new(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    /// <summary>이 플레이어가 고른 색 — 상황판이 얼굴을 찾을 때 읽는다. (#432)</summary>
    public PlayerColorSet Colors => m_colors.Value;

    private BodyTint m_tint;
    private readonly Color[] m_buffer = new Color[3]; // 인덱스 = EBodyPart

    private void Awake() => m_tint = GetComponent<BodyTint>();

    public override void OnNetworkSpawn()
    {
        // 여기서 쓴 값은 <b>스폰 페이로드에 실려</b> 나간다 — 그래서 남의 화면도 첫 프레임부터 제 색이다.
        // 오너 쓰기로는 이 시점에 값이 없어(오너는 스폰 메시지를 받은 뒤에야 쓴다) 기본색이 먼저 갔다. (#790)
        if (IsServer)
        {
            m_colors.Value = ResolveSpawnColors(out bool resolved);
            m_assigned.Value = resolved;
        }

        if (IsOwner)
        {
            CosmeticLoadout.OnPlayerColorChanged += HandleOwnerColorChanged;

            // 안전망 — 명부 보고가 아직 안 닿은 채로 스폰되면(입장 직후 게임 씬으로 바로 들어오는 경합)
            // 서버가 내 색을 모른다. 그때만 한 박자 늦게 고쳐진다. 값이 같으면 NetworkVariable이
            // 스스로 무시하므로 정상 경로에서는 대역폭을 먹지 않는다.
            if (!IsServer)
                ReportColorsRpc(PlayerColorSet.FromSettings());
        }

        m_colors.OnValueChanged += HandleColorsChanged;
        m_assigned.OnValueChanged += HandleAssignedChanged;
        Apply(); // late-join은 값을 복제만 받고 OnValueChanged를 못 받는다
    }

    /// <summary>
    /// 서버가 아는 이 플레이어의 색 — 명부가 로비 입장 때 각자에게서 받아 둔 값이다 (#432).
    /// 명부가 없거나(세션 없이 씬 직접 Play) 보고가 아직 안 닿았으면 로컬 설정으로 떨어진다 —
    /// 그 경우 서버가 곧 오너라 로컬 설정이 맞는 값이다.
    /// </summary>
    private PlayerColorSet ResolveSpawnColors(out bool resolved)
    {
        resolved = true;

        SessionRoster roster = App.Game.Roster;
        if (roster != null && roster.TryGetEntry(OwnerClientId, out LobbyPlayerEntry entry))
            return entry.Colors;

        // ⚠ 남의 색을 로컬 설정으로 채우면 <b>호스트 색이 그 사람에게 입혀진다</b> — 틀린 값을
        // 확신 있게 칠하는 것보다 기본색으로 두고 오너 보고를 기다리는 쪽이 낫다.
        if (IsOwner)
            return PlayerColorSet.FromSettings();

        // 여기 걸리면 그 사람 로봇이 기본색으로 한 번 보이고 오너 보고 뒤에 제 색으로 바뀐다 (#790).
        // 정상 흐름(로비를 거쳐 옴)에서는 나지 않는다 — 나면 스폰이 명부 보고를 앞질렀다는 뜻이다.
        Debug.LogWarning(
            $"[{nameof(PlayerCosmetics)}] 명부에 색이 아직 없어 기본색으로 스폰했다 "
                + $"— 클라 {OwnerClientId}, 명부 {(App.Game.Roster == null ? "없음" : App.Game.Roster.Players.Count + "명")} (#790)",
            this
        );
        resolved = false;
        return default;
    }

    public override void OnNetworkDespawn()
    {
        m_colors.OnValueChanged -= HandleColorsChanged;
        m_assigned.OnValueChanged -= HandleAssignedChanged;

        // 오너만 구독했지만 무조건 뗀다 — 아니면 죽은 로봇을 가리키는 static 구독이 쌓인다
        CosmeticLoadout.OnPlayerColorChanged -= HandleOwnerColorChanged;
    }

    // 색을 고치는 UI는 지금 로비에만 있어(PlayerColorPanel) 스폰된 뒤 이 경로가 도는 구성은 없다.
    // 그래도 남겨 둔다 — 없애면 나중에 게임 중 변경이 생겼을 때 조용히 반영되지 않는다.
    private void HandleOwnerColorChanged(EBodyPart _)
    {
        if (!IsOwner || !IsSpawned)
            return;

        PlayerColorSet colors = PlayerColorSet.FromSettings();
        if (IsServer)
            m_colors.Value = colors; // 호스트 자신 — RPC를 돌 필요가 없다
        else
            ReportColorsRpc(colors);
    }

    // 값의 주인이 서버가 됐으므로 원격 오너는 보고만 한다. 코스메틱이라 서버가 검증하지 않는다 (#432).
    [Rpc(SendTo.Server)]
    private void ReportColorsRpc(PlayerColorSet colors)
    {
        m_colors.Value = colors;
        m_assigned.Value = true; // 이제 진짜 그 사람 색이다 — 남들도 몸을 그리기 시작한다
    }

    private void HandleAssignedChanged(bool previous, bool current) => Apply();

    private void HandleColorsChanged(PlayerColorSet previous, PlayerColorSet current) => Apply();

    private void Apply()
    {
        if (m_palette == null)
        {
            Debug.LogWarning($"[{nameof(PlayerCosmetics)}] 색 팔레트가 연결되지 않았습니다 (#432)", this);
            return;
        }

        PlayerColorSet colors = m_colors.Value;

        if (!m_assigned.Value)
        {
            // 오너는 자기 색을 언제나 안다 — 서버 값이 없어도 로컬 설정으로 즉시 칠한다.
            // (여기서 숨기면 내 1인칭 팔까지 사라진다)
            if (!IsOwner)
            {
                m_tint.SetRendering(false);
                return;
            }

            colors = PlayerColorSet.FromSettings();
        }

        m_tint.SetRendering(true);

        for (int i = 0; i < m_buffer.Length; i++)
            m_buffer[i] = m_palette.Get(colors[(EBodyPart)i]);

        m_tint.SetBase(m_buffer, m_buffer[(int)EBodyPart.Torso]);
    }
}
