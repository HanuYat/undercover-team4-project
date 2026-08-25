using System.Collections.Generic;
using TMPro;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 팀 상황판 (#720) — Tab 홀드 중 동료 상태와 이번 라운드 수배 몽타주를 함께 띄운다.
/// 여는 것은 <see cref="PlayerTeamStatusInput"/>(오너 로컬)이고 여기는 그리기만 한다.
///
/// 커서를 풀지 않는다 — 표시용 창인데 커서가 풀리면 E가 통째로 막힌다(#352). 같은 이유로
/// ESC 스택에도 쌓지 않는다. 새 동기화도 없다 — 체력·무력화·이름이 전부 이미 NetworkVariable이다.
/// </summary>
public class TeamStatusPanel : PanelBase
{
    public override bool CanCloseWithESC => false;

    public override bool IsStackable => false;

    private const string k_table = "HudTable";

    [Header("머리말")]
    [SerializeField] private TextMeshProUGUI m_titleText;
    [SerializeField] private TextMeshProUGUI m_membersLabel;
    [SerializeField] private TextMeshProUGUI m_montageLabel;

    [Header("파티원")]
    [SerializeField] private RectTransform m_rowContainer;
    [SerializeField] private TeamStatusRowView m_rowPrefab;

    // 행은 지우지 않고 재사용한다 — 홀드마다 파괴·생성하면 레이아웃이 매번 다시 돈다.
    private readonly List<TeamStatusRowView> m_rows = new List<TeamStatusRowView>();
    private readonly List<NetworkObject> m_players = new List<NetworkObject>();

    // 직전 구성과 비교할 임시 자리 — 매 프레임 도는 경로라 새로 할당하지 않는다.
    private readonly List<NetworkObject> m_scratch = new List<NetworkObject>();

    // 인원이 바뀔 때만 찾아 둔다 — 아니면 떠 있는 동안 매 프레임 인원수만큼 GetComponent가 돈다.
    private readonly List<PlayerHealth> m_health = new List<PlayerHealth>();
    private readonly List<PlayerIncapacitation> m_incapacitation = new List<PlayerIncapacitation>();
    private readonly List<PlayerNameTag> m_nameTags = new List<PlayerNameTag>();
    private readonly List<PlayerCosmetics> m_cosmetics = new List<PlayerCosmetics>();
    private readonly List<PlayerAccessories> m_accessories = new List<PlayerAccessories>();

    public override void OpenPanel()
    {
        ApplyLabels();
        Rebuild();
        base.OpenPanel();
    }

    // 열 때마다 채운다 — 닫힌 사이에 언어가 바뀌었을 수 있다. 매 프레임 도는 경로가 아니라 비용도 무해하다.
    private void ApplyLabels()
    {
        if (m_titleText != null)
            m_titleText.text = LocalizedStrings.Get(k_table, "Hud.Team.Title");

        if (m_membersLabel != null)
            m_membersLabel.text = LocalizedStrings.Get(k_table, "Hud.Team.Section.Members");

        if (m_montageLabel != null)
            m_montageLabel.text = LocalizedStrings.Get(k_table, "Hud.Team.Section.Wanted");
    }

    private void Update()
    {
        if (!IsOpened)
            return;

        // 인원 변화(합류·이탈)는 홀드 중에도 일어날 수 있다.
        if (CollectPlayers())
            Rebuild();
        else
            RefreshRows();
    }

    // 스폰된 플레이어 오브젝트를 모은다 — 이름·체력·상태가 전부 여기 달려 있어 명부와 합칠 것이 없다.
    // 내 것은 뺀다 — 내 체력은 좌하단 기름통이 상시로 보여 준다.
    // 반환값은 "인원 구성이 바뀌었는가".
    private bool CollectPlayers()
    {
        NetworkManager manager = NetworkManager.Singleton;
        if (manager == null || manager.SpawnManager == null)
        {
            bool had = m_players.Count > 0;
            m_players.Clear();
            return had;
        }

        IReadOnlyList<NetworkObject> spawned = manager.SpawnManager.PlayerObjects;

        m_scratch.Clear();
        for (int i = 0; i < spawned.Count; i++)
        {
            NetworkObject player = spawned[i];
            if (player == null || player.IsLocalPlayer)
                continue;

            m_scratch.Add(player);
        }

        // 걸러낸 뒤에 비교한다 — 원본 목록과 대조하면 내 것이 빠진 만큼 인덱스가 어긋난다.
        bool changed = m_scratch.Count != m_players.Count;

        if (!changed)
        {
            for (int i = 0; i < m_scratch.Count; i++)
            {
                if (m_scratch[i] == m_players[i])
                    continue;

                changed = true;
                break;
            }
        }

        if (!changed)
            return false;

        m_players.Clear();
        m_players.AddRange(m_scratch);

        return true;
    }

    private void Rebuild()
    {
        CollectPlayers();

        m_health.Clear();
        m_incapacitation.Clear();
        m_nameTags.Clear();
        m_cosmetics.Clear();
        m_accessories.Clear();
        for (int i = 0; i < m_players.Count; i++)
        {
            NetworkObject player = m_players[i];
            m_health.Add(player != null ? player.GetComponent<PlayerHealth>() : null);
            m_incapacitation.Add(player != null ? player.GetComponent<PlayerIncapacitation>() : null);
            m_nameTags.Add(player != null ? player.GetComponent<PlayerNameTag>() : null);
            m_cosmetics.Add(player != null ? player.GetComponent<PlayerCosmetics>() : null);
            m_accessories.Add(player != null ? player.GetComponent<PlayerAccessories>() : null);
        }

        if (m_rowContainer == null || m_rowPrefab == null)
            return;

        while (m_rows.Count < m_players.Count)
            m_rows.Add(Instantiate(m_rowPrefab, m_rowContainer));

        for (int i = 0; i < m_rows.Count; i++)
        {
            bool used = i < m_players.Count;
            m_rows[i].gameObject.SetActive(used);
            if (!used)
                continue;

            // 행은 재사용하므로 여기서 한 번 덮어쓴다 — 아니면 자리를 물려받은 카드에 떠난 사람
            // 이름이 남는다. 아직 안 온 이름은 RefreshRows가 채운다.
            m_rows[i].SetName(NameOf(i));

            // 로비에서 구운 얼굴을 그대로 쓴다 — 게임 씬에서 다시 구우면 맵 조명을 타 어둡게 나온다.
            // 사람마다 고른 색이 다르므로 그 사람 색으로 찾는다 (#432)
            m_rows[i].SetPortrait(PortraitOf(i));
        }

        RefreshRows();
    }

    // 색을 아직 못 읽었으면(스폰 직후) 얼굴 없이 둔다 — 남의 얼굴을 대신 띄우지 않는다
    private Texture PortraitOf(int index)
    {
        PlayerCosmetics cosmetics = index < m_cosmetics.Count ? m_cosmetics[index] : null;
        if (cosmetics == null)
            return null;

        PlayerAccessories accessories = index < m_accessories.Count ? m_accessories[index] : null;
        return LobbyPortraitStage.GetSessionPortrait(
            cosmetics.Colors,
            accessories != null ? accessories.Accessories : default
        );
    }

    private void RefreshRows()
    {
        for (int i = 0; i < m_players.Count && i < m_rows.Count; i++)
        {
            if (m_players[i] == null)
                continue;

            PlayerHealth health = i < m_health.Count ? m_health[i] : null;
            int max = health != null ? health.MaxHp : 0;
            int hp = health != null ? health.CurrentHp : 0;

            m_rows[i].SetStatus(hp, max, StateOf(i < m_incapacitation.Count ? m_incapacitation[i] : null));

            // 이름은 비어 있는 동안만 다시 묻는다 — 오너 쓰기 NetworkVariable이라 스폰과 같은
            // 프레임에 행을 만들면 아직 안 와 있고, 그때 한 번만 넣으면 그대로 빈 칸으로 굳는다.
            // 채워진 뒤엔 바뀌지 않으므로 매 프레임 문자열을 만들지 않는다.
            if (!m_rows[i].HasName)
                m_rows[i].SetName(NameOf(i));

            // 얼굴도 같은 이유로 다시 묻는다 — 색은 오너 쓰기 NetworkVariable이라 스폰과 같은
            // 프레임에는 아직 안 와 있고, 그때 한 번만 넣으면 빈 칸으로 굳는다. (#432)
            if (!m_rows[i].HasPortrait)
                m_rows[i].SetPortrait(PortraitOf(i));
        }
    }

    private string NameOf(int index)
    {
        PlayerNameTag tag = index < m_nameTags.Count ? m_nameTags[index] : null;
        return tag != null ? tag.DisplayName : string.Empty;
    }

    // 기절·오검거 매달기는 스스로 풀려서 생존으로 묶는다. 동료가 움직여야 하는 것은 납치와 기능 정지뿐이다.
    // Down은 쓰지 않는다 — GDD 10-2대로 #524 이후 발생하지 않고 설정하는 곳도 없다.
    private static ETeamMemberState StateOf(PlayerIncapacitation incapacitation)
    {
        if (incapacitation == null)
            return ETeamMemberState.Alive;

        switch (incapacitation.Cause)
        {
            case IncapacitationCause.Die:
                return ETeamMemberState.Dead;

            case IncapacitationCause.Abducted:
                return ETeamMemberState.Abducted;

            default:
                return ETeamMemberState.Alive;
        }
    }
}
