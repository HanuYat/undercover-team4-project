using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 플레이어 로봇 색 (#432) — <b>순수 코스메틱</b>이다. 식별·게임플레이 기능이 아니라 색칠놀이라
/// 서버가 값을 검증하지 않고, NPC 몽타주 축(<c>AppearanceProfile</c>)과도 무관하다.
///
/// 오너가 자기 로컬 설정(<see cref="GameSettings.PlayerColorIndex"/>)을 <b>오너 쓰기 NetworkVariable</b>로
/// 올리고 전 클라가 각자 칠한다 — 서버가 모르는 로컬 값이라 <see cref="PlayerNameTag"/>와 같은 구조다.
/// 이름표에 얹지 않고 부품을 나눈 것은 그쪽이 '이름표'를 넘어서지 않게 하려는 것이다.
///
/// 색은 <b>인덱스</b>로 오간다 — 팔레트가 정본이라 대역폭이 작고 값 검증도 단순하다.
/// 칠하기는 <see cref="BodyTint"/>의 밑색 채널에 맡긴다: 직접 MaterialPropertyBlock을 들면
/// 감전 발광(#477)과 서로의 값을 덮어쓰고, 연출이 걷힐 때 이 색까지 함께 지워진다.
///
/// 로비 쪽 경로는 이 부품과 겹치지 않는다 — 로비에는 플레이어 오브젝트가 없어(#214)
/// 로스터(<see cref="LobbyPlayerEntry"/>)가 나르고, 게임 씬에는 그 로스터가 없다. 값의 출처는 양쪽 모두 하나다.
/// </summary>
[RequireComponent(typeof(BodyTint))]
public class PlayerCosmetics : NetworkBehaviour
{
    [Tooltip("색 팔레트 — 로비 팔레트와 반드시 같은 에셋을 물릴 것 (인덱스가 어긋나면 색이 갈린다)")]
    [SerializeField] private PlayerColorPalette m_palette;

    private readonly NetworkVariable<byte> m_colorIndex = new(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Owner
    );

    private BodyTint m_tint;

    private void Awake() => m_tint = GetComponent<BodyTint>();

    public override void OnNetworkSpawn()
    {
        if (IsOwner)
        {
            // 씬을 넘어오며 플레이어가 다시 스폰되므로 매번 현재 설정값에서 시작한다 (PlayerNameTag와 동일)
            m_colorIndex.Value = ToIndex(GameSettings.PlayerColorIndex);
            GameSettings.OnPlayerColorChanged += HandleOwnerColorChanged;
        }

        m_colorIndex.OnValueChanged += HandleColorChanged;

        // 늦게 들어온 클라는 이미 정해진 값을 복제받고 OnValueChanged를 못 받는다 — 지금 한 번 칠한다.
        Apply();
    }

    public override void OnNetworkDespawn()
    {
        m_colorIndex.OnValueChanged -= HandleColorChanged;

        // 오너만 구독했지만 무조건 뗀다 — 구독하지 않았으면 no-op이고, 씬 전환마다 다시 스폰되므로
        // 여기서 빠지면 죽은 로봇을 가리키는 static 구독이 쌓인다 (PlayerNameTag와 같은 이유).
        GameSettings.OnPlayerColorChanged -= HandleOwnerColorChanged;
    }

    private void HandleOwnerColorChanged(int index)
    {
        if (IsOwner && IsSpawned)
            m_colorIndex.Value = ToIndex(index);
    }

    private void HandleColorChanged(byte previous, byte current) => Apply();

    private void Apply()
    {
        if (m_palette == null)
        {
            Debug.LogWarning(
                $"[{nameof(PlayerCosmetics)}] 색 팔레트가 연결되지 않았습니다 — 기본 색으로 진행합니다 (#432)",
                this
            );
            return;
        }

        m_tint.SetBase(m_palette.Get(m_colorIndex.Value));
    }

    // 팔레트 길이는 여기서 보지 않는다 — 범위 밖 인덱스는 PlayerColorPalette.Get이 자르므로,
    // 팔레트를 나중에 줄여도 모두가 같은 색을 본다.
    private static byte ToIndex(int index) => (byte)Mathf.Clamp(index, 0, byte.MaxValue);
}
