using System.Collections.Generic;
using TMPro;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 팀 상황판 (#720) — Tab을 누르고 있는 동안 파티원 상태와 이번 라운드 수배 몽타주를 함께 띄운다.
/// 여는 것은 <see cref="PlayerTeamStatusInput"/>(오너 로컬)이고, 여기는 그리기만 한다.
///
/// <b>커서를 풀지 않는다</b> — 누를 것이 없는 표시용 창이고, 커서가 풀리면
/// <see cref="PlayerInteractor.HandleInteract"/>가 E를 통째로 막는다(#352). 그래서
/// <see cref="IsStackable"/>도 false다 — ESC 스택에 쌓이는 모달이 아니라 잠깐 겹치는 표시다.
///
/// <b>새 동기화가 없다</b> — HP·무력화·이름이 전부 이미 NetworkVariable이라 값만 읽으면 된다.
/// 떠 있는 동안에만 갱신하므로 폴링 비용도 그 순간에만 든다.
///
/// 수배 몽타주는 <see cref="WantedListView"/>를 그대로 얹는다(본부 의존이 없다). 현장은 <b>초상화만</b>
/// 보이게 행 프리팹에서 이름·몽타주문·현상금 칸을 비워 둔다 — 대조 자료는 본부 몫으로 남긴다.
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

    [Header("대원 얼굴")]
    [Tooltip("대원 카드에 들어갈 얼굴을 굽는 무대 — 로비 명단과 같은 것을 쓴다 (#598)")]
    [SerializeField] private LobbyPortraitStage m_portraitStage;

    [Header("파티원")]
    [SerializeField] private RectTransform m_rowContainer;
    [SerializeField] private TeamStatusRowView m_rowPrefab;

    // 만든 행은 지우지 않고 재사용한다 — 홀드할 때마다 파괴·생성하면 매번 레이아웃이 다시 돈다.
    private readonly List<TeamStatusRowView> m_rows = new List<TeamStatusRowView>();
    private readonly List<NetworkObject> m_players = new List<NetworkObject>();

    // 걸러낸 결과를 담아 직전 구성과 비교하는 임시 자리 — 매 프레임 도는 경로라 새로 할당하지 않는다.
    private readonly List<NetworkObject> m_scratch = new List<NetworkObject>();

    public override void OpenPanel()
    {
        ApplyLabels();
        Rebuild();
        base.OpenPanel();
    }

    // 고정 문구는 열 때마다 채운다 — 닫혀 있는 사이에 언어가 바뀌었을 수 있고,
    // 여는 순간은 프레임마다 도는 경로가 아니라 조회 비용이 문제되지 않는다.
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

    // 스폰된 플레이어 오브젝트를 모은다 — 명부(SessionRoster) 대신 이쪽을 쓰는 이유는 이름·HP·상태가
    // 전부 이 오브젝트에 달려 있어 합칠 것이 없기 때문이다. 아직 스폰되지 않은 접속자는 보여 줄 값도 없다.
    //
    // <b>내 것은 뺀다</b> — 내 HP는 좌하단 기름통이 상시로 보여 주고 내 상태는 내가 이미 안다.
    // 상황판은 무전으로만 알 수 있던 남의 상태를 보는 창이다.
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

            m_rows[i].SetName(NameOf(m_players[i]));

            // 얼굴은 여는 시점에만 넣으면 된다 — 무대가 한 번 굽고 끝나는 정적 텍스처다.
            m_rows[i].SetPortrait(m_portraitStage != null ? m_portraitStage.Portrait : null);
        }

        RefreshRows();
    }

    private void RefreshRows()
    {
        for (int i = 0; i < m_players.Count && i < m_rows.Count; i++)
        {
            NetworkObject player = m_players[i];
            if (player == null)
                continue;

            var health = player.GetComponent<PlayerHealth>();
            int max = health != null ? health.MaxHp : 0;
            int hp = health != null ? health.CurrentHp : 0;

            m_rows[i].SetStatus(hp, max, StateOf(player));
        }
    }

    private static string NameOf(NetworkObject player)
    {
        if (player == null)
            return string.Empty;

        var tag = player.GetComponent<PlayerNameTag>();
        return tag != null ? tag.DisplayName : string.Empty;
    }

    // 기절·매달림은 스스로 풀리는 상태라 "정상"으로 묶는다 — 동료가 달려가야 하는 것은 다운과 사망뿐이다.
    private static ETeamMemberState StateOf(NetworkObject player)
    {
        var incapacitation = player.GetComponent<PlayerIncapacitation>();
        if (incapacitation == null)
            return ETeamMemberState.Normal;

        if (incapacitation.IsDead)
            return ETeamMemberState.Dead;

        return incapacitation.IsDowned ? ETeamMemberState.Down : ETeamMemberState.Normal;
    }
}
