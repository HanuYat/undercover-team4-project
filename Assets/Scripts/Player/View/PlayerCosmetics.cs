using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 플레이어 로봇 색 (#432) — 순수 코스메틱. 오너가 자기 로컬 설정을 오너 쓰기 NetworkVariable로
/// 올리고 전 클라가 각자 칠한다 (<see cref="PlayerNameTag"/>와 같은 구조, 부품만 나눴다).
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
        NetworkVariableWritePermission.Owner
    );

    /// <summary>이 플레이어가 고른 색 — 상황판이 얼굴을 찾을 때 읽는다. (#432)</summary>
    public PlayerColorSet Colors => m_colors.Value;

    private BodyTint m_tint;
    private readonly Color[] m_buffer = new Color[3]; // 인덱스 = EBodyPart

    private void Awake() => m_tint = GetComponent<BodyTint>();

    public override void OnNetworkSpawn()
    {
        if (IsOwner)
        {
            // 씬을 넘어오며 다시 스폰되므로 매번 현재 설정값에서 시작한다
            m_colors.Value = PlayerColorSet.FromSettings();
            GameSettings.OnPlayerColorChanged += HandleOwnerColorChanged;
        }

        m_colors.OnValueChanged += HandleColorsChanged;
        Apply(); // late-join은 값을 복제만 받고 OnValueChanged를 못 받는다
    }

    public override void OnNetworkDespawn()
    {
        m_colors.OnValueChanged -= HandleColorsChanged;

        // 오너만 구독했지만 무조건 뗀다 — 아니면 죽은 로봇을 가리키는 static 구독이 쌓인다
        GameSettings.OnPlayerColorChanged -= HandleOwnerColorChanged;
    }

    private void HandleOwnerColorChanged(EBodyPart _)
    {
        if (IsOwner && IsSpawned)
            m_colors.Value = PlayerColorSet.FromSettings();
    }

    private void HandleColorsChanged(PlayerColorSet previous, PlayerColorSet current) => Apply();

    private void Apply()
    {
        if (m_palette == null)
        {
            Debug.LogWarning($"[{nameof(PlayerCosmetics)}] 색 팔레트가 연결되지 않았습니다 (#432)", this);
            return;
        }

        PlayerColorSet colors = m_colors.Value;
        for (int i = 0; i < m_buffer.Length; i++)
            m_buffer[i] = m_palette.Get(colors[(EBodyPart)i]);

        m_tint.SetBase(m_buffer, m_buffer[(int)EBodyPart.Torso]);
    }
}
