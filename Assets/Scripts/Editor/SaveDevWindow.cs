using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 세이브 편집기 (#373) — '이어하기'를 테스트할 판을 손으로 만들어 올리는 개발 도구. 메뉴: Tools/세이브 편집기
///
/// 이 게임의 세이브는 로컬 파일이 아니라 UGS Cloud Save의 키 하나이고, 저장소가 로그인 계정 단위다 —
/// 그래서 <b>플레이 모드에서 로그인이 끝난 뒤에만</b> 읽고 쓸 수 있다(타이틀 화면이면 조건이 갖춰져 있다).
/// 쓰는 경로는 <see cref="SaveService.DevOverwriteAsync"/> 하나뿐이라 실제 저장과 키·직렬화가 같다.
/// 즉 여기서 만든 세이브는 진짜로 플레이해서 만든 세이브와 구분되지 않는다.
///
/// <b>사용 순서</b>
///  ① Play → 타이틀에서 로그인이 끝나기를 기다린다
///  ② 이 창에 값을 채우고 '이 값으로 덮어쓰기'
///  ③ Play를 껐다 켠다 → 타이틀의 '이어하기'가 그 값으로 켜진다
/// ③이 필요한 이유: 세이브 유무 조회(SessionPanel)는 로그인 직후 한 번만 돈다.
///
/// 세이브는 <b>지금 로그인한 계정의 것</b>이다. 다른 계정으로 방을 만들면 보이지 않고,
/// 지갑도 PlayerId가 일치해야 복원된다 — 그래서 '내 PlayerId 추가' 버튼이 있다.
/// </summary>
public class SaveDevWindow : EditorWindow
{
    [SerializeField]
    private int m_round = RoundProgress.k_firstRound;

    [SerializeField]
    private int m_teamFund;

    [SerializeField]
    private int m_mapIndex;

    [SerializeField]
    private List<ItemBase> m_carried = new List<ItemBase>();

    [SerializeField]
    private List<EInstallable> m_installables = new List<EInstallable>();

    [SerializeField]
    private List<PlayerSaveEntry> m_players = new List<PlayerSaveEntry>();

    [SerializeField]
    private Vector2 m_scroll;

    [MenuItem("Tools/세이브 편집기")]
    private static void Open() => GetWindow<SaveDevWindow>("세이브 편집기");

    // 로그인은 런타임 상태다 — 에디트 모드에서는 App을 아예 건드리지 않는다.
    private static AuthBootstrap Auth => Application.isPlaying ? App.Net.Auth : null;

    private static bool IsReady => Auth != null && Auth.IsSignedIn;

    // '지금 판 상태로 저장'만 조건이 하나 더 붙는다 — 세션 밖(타이틀)에서 부르면 상주 홀더가 아직 없어
    // 기본값(1라운드·자금 0·빈 구매 목록)이 '저장 완료' 로그와 함께 세이브를 덮는다. 실패로 보이지도 않는다.
    private static bool CanSaveLive
    {
        get
        {
            NetworkManager nm = NetworkManager.Singleton;
            return IsReady && nm != null && nm.IsListening && nm.IsServer;
        }
    }

    private void OnGUI()
    {
        DrawStatus();

        m_scroll = EditorGUILayout.BeginScrollView(m_scroll);

        m_round = Mathf.Max(RoundProgress.k_firstRound, EditorGUILayout.IntField("라운드", m_round));
        m_teamFund = EditorGUILayout.IntField("팀 자금", m_teamFund);
        m_mapIndex = Mathf.Max(0, EditorGUILayout.IntField("맵 인덱스", m_mapIndex));

        DrawCarried();
        DrawInstallables();
        DrawPlayers();

        EditorGUILayout.EndScrollView();

        DrawButtons();
    }

    private void DrawStatus()
    {
        if (!Application.isPlaying)
        {
            EditorGUILayout.HelpBox(
                "플레이 모드가 아니다 — 값 편집은 되지만 올리고 내리는 건 안 된다.\n"
                    + "타이틀 씬을 Play해 로그인이 끝나면 버튼이 켜진다.",
                MessageType.Info
            );
            return;
        }

        if (!IsReady)
        {
            EditorGUILayout.HelpBox("로그인 대기 중 — 타이틀에서 로그인이 끝나면 버튼이 켜진다.", MessageType.Warning);
            return;
        }

        EditorGUILayout.HelpBox(
            $"로그인됨 / PlayerId: {Auth.PlayerId}\n덮어쓴 뒤 Play를 껐다 켜야 타이틀의 '이어하기'에 반영된다.",
            MessageType.None
        );
    }

    // 소지형 — 세이브에 적히는 id는 프리팹 이름이라(SaveItemLookup) 프리팹을 그대로 끌어다 놓게 한다.
    private void DrawCarried()
    {
        EditorGUILayout.Space();
        EditorGUILayout.LabelField($"구매한 소지형 ({m_carried.Count})", EditorStyles.boldLabel);

        int removeAt = -1;
        for (int i = 0; i < m_carried.Count; i++)
        {
            EditorGUILayout.BeginHorizontal();
            m_carried[i] = (ItemBase)
                EditorGUILayout.ObjectField(m_carried[i], typeof(ItemBase), false);
            if (GUILayout.Button("삭제", GUILayout.Width(44)))
                removeAt = i;
            EditorGUILayout.EndHorizontal();
        }

        if (removeAt >= 0)
            m_carried.RemoveAt(removeAt);

        // 빈 칸에 프리팹을 떨구면 목록에 추가된다(칸 자신은 계속 비어 있다). 같은 아이템 중복도 그대로 허용.
        var added = (ItemBase)EditorGUILayout.ObjectField("추가", null, typeof(ItemBase), false);
        if (added != null)
            m_carried.Add(added);

        WarnUnregistered();
    }

    // 복원은 NGO 등록 명부에 있는 프리팹만 된다 — 미등록 프리팹은 올라가기는 하고 복원 시점에 조용히 사라진다.
    // 명부는 런타임 상태라 플레이 중에만 물어볼 수 있다(선형 탐색이지만 목록이 짧아 그릴 때마다 훑어도 된다).
    private void WarnUnregistered()
    {
        if (!Application.isPlaying)
            return;

        var unknown = new List<string>();
        foreach (ItemBase item in m_carried)
        {
            string id = SaveItemLookup.GetId(item);
            if (!string.IsNullOrEmpty(id) && SaveItemLookup.Find(id) == null)
                unknown.Add(id);
        }

        if (unknown.Count > 0)
        {
            EditorGUILayout.HelpBox(
                $"DefaultNetworkPrefabs에 없어 복원되지 않는다: {string.Join(", ", unknown)}",
                MessageType.Warning
            );
        }
    }

    private void DrawInstallables()
    {
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("구매한 설치형", EditorStyles.boldLabel);

        foreach (EInstallable value in Enum.GetValues(typeof(EInstallable)))
        {
            if (value == EInstallable.None)
                continue;

            bool had = m_installables.Contains(value);
            bool has = EditorGUILayout.ToggleLeft(value.ToString(), had);
            if (has == had)
                continue;

            if (has)
                m_installables.Add(value);
            else
                m_installables.Remove(value);
        }
    }

    private void DrawPlayers()
    {
        EditorGUILayout.Space();
        EditorGUILayout.LabelField($"개인 지갑 ({m_players.Count})", EditorStyles.boldLabel);

        int removeAt = -1;
        for (int i = 0; i < m_players.Count; i++)
        {
            PlayerSaveEntry entry = m_players[i];

            EditorGUILayout.BeginHorizontal();
            entry.PlayerId = EditorGUILayout.TextField(entry.PlayerId);
            entry.Balance = EditorGUILayout.IntField(entry.Balance, GUILayout.Width(80));
            if (GUILayout.Button("삭제", GUILayout.Width(44)))
                removeAt = i;
            EditorGUILayout.EndHorizontal();
        }

        if (removeAt >= 0)
            m_players.RemoveAt(removeAt);

        EditorGUILayout.BeginHorizontal();

        // 지갑 복원은 PlayerId가 일치해야 걸린다 — 손으로 적다 틀리면 "새로 합류한 사람"으로 취급돼 0에서 시작한다.
        using (new EditorGUI.DisabledScope(!IsReady))
        {
            if (GUILayout.Button("내 PlayerId 추가"))
                m_players.Add(new PlayerSaveEntry { PlayerId = Auth.PlayerId, Balance = 0 });
        }

        if (GUILayout.Button("빈 칸 추가"))
            m_players.Add(new PlayerSaveEntry());

        EditorGUILayout.EndHorizontal();
    }

    private void DrawButtons()
    {
        EditorGUILayout.Space();

        using (new EditorGUI.DisabledScope(!IsReady))
        {
            if (GUILayout.Button("이 값으로 덮어쓰기"))
                OverwriteAsync().Forget();

            EditorGUILayout.BeginHorizontal();

            // 창에 적어 둔 값을 통째로 날리는 방향이라 한 번 묻는다.
            if (
                GUILayout.Button("클라우드에서 불러오기")
                && EditorUtility.DisplayDialog(
                    "클라우드에서 불러오기",
                    "창에 적어 둔 값이 저장된 세이브로 전부 덮인다. 계속할까?",
                    "불러오기",
                    "취소"
                )
            )
            {
                LoadAsync().Forget();
            }

            // 지금 진행 중인 판을 그대로 굳힌다 — 실제 저장 경로(SaveAsync)다. 세션이 서 있을 때만 켜진다.
            using (new EditorGUI.DisabledScope(!CanSaveLive))
            {
                if (GUILayout.Button("지금 판 상태로 저장"))
                    SaveService.SaveAsync().Forget();
            }

            if (GUILayout.Button("세이브 지우기"))
                SaveService.DeleteAsync().Forget();

            EditorGUILayout.EndHorizontal();
        }
    }

    private async UniTaskVoid OverwriteAsync()
    {
        await SaveService.DevOverwriteAsync(Build());
        Repaint();
    }

    private async UniTaskVoid LoadAsync()
    {
        if (await SaveService.RefreshAsync())
            FillFrom(SaveService.DevKnown);
        else
            Debug.Log("[세이브 편집기] 불러올 세이브가 없다 — 창의 값은 그대로 둔다");

        Repaint();
    }

    private SessionSaveData Build()
    {
        var carried = new List<string>(m_carried.Count);
        foreach (ItemBase item in m_carried)
        {
            string id = SaveItemLookup.GetId(item);
            if (!string.IsNullOrEmpty(id))
                carried.Add(id);
        }

        var installables = new List<string>(m_installables.Count);
        foreach (EInstallable installable in m_installables)
            installables.Add(installable.ToString());

        var players = new List<PlayerSaveEntry>(m_players.Count);
        foreach (PlayerSaveEntry entry in m_players)
        {
            if (string.IsNullOrWhiteSpace(entry.PlayerId))
                continue;

            players.Add(
                new PlayerSaveEntry { PlayerId = entry.PlayerId.Trim(), Balance = entry.Balance }
            );
        }

        return new SessionSaveData
        {
            Round = m_round,
            TeamFund = m_teamFund,
            MapIndex = m_mapIndex,
            CarriedItems = carried.ToArray(),
            Installables = installables.ToArray(),
            Players = players.ToArray(),
        };
    }

    private void FillFrom(SessionSaveData data)
    {
        if (data == null)
            return;

        m_round = data.Round;
        m_teamFund = data.TeamFund;
        m_mapIndex = data.MapIndex;

        m_carried.Clear();
        foreach (string id in data.CarriedItems)
        {
            ItemBase prefab = SaveItemLookup.Find(id);
            if (prefab != null)
                m_carried.Add(prefab);
            else
                Debug.LogWarning($"[세이브 편집기] 소지형 '{id}' 프리팹을 못 찾아 목록에서 뺐다 — 그대로 덮어쓰면 사라진다");
        }

        m_installables.Clear();
        foreach (string installableName in data.Installables)
        {
            if (Enum.TryParse(installableName, out EInstallable installable) && installable != EInstallable.None)
                m_installables.Add(installable);
            else
                Debug.LogWarning($"[세이브 편집기] 설치형 '{installableName}'을(를) 알 수 없어 뺐다");
        }

        m_players.Clear();
        foreach (PlayerSaveEntry entry in data.Players)
            m_players.Add(new PlayerSaveEntry { PlayerId = entry.PlayerId, Balance = entry.Balance });
    }
}
