using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 다음 라운드로 갈 게임 맵의 선택 상태 — 세션 내내 유지되는 상주 홀더. (#578)
/// SessionObjectSpawner가 세션 시작 시 1회 스폰(destroyWithScene:false)한다. (#214 §6 이월 구조)
///
/// <b>Shop 씬에 두지 않는 이유</b>: 전환이 일어나는 순간이 곧 Shop이 언로드되는 순간이라,
/// <see cref="AppHelper.ToSceneName"/>이 씬 이름을 물으려는 그때 홀더가 이미 죽어 있다.
/// 조작·표시는 Shop 씬의 콘솔(<see cref="MapSelectButton"/>·<see cref="MapSelectionView"/>)이 맡는다 —
/// 상태 보유자와 상호작용을 나누는 구조는 CCTVSwitcher/CCTVSwitchButton과 같다. (#362)
///
/// <b>복제하는 이유는 표시뿐이다.</b> NGO 씬 동기화는 서버가 이름으로 로드하고 클라는 끌려오므로
/// (AppHelper.LoadSceneAsync가 클라의 전환 요청을 아예 거부한다) 전환만 보면 서버만 정확하면 된다.
/// 그래도 동기화하는 건 전 클라 화면에 "다음 맵이 어디인지"가 같게 떠야 하기 때문이다.
///
/// 맵 목록은 이 컴포넌트의 인스펙터 배열이 유일한 주인이다 — 복제하는 값은 인덱스 하나뿐이고
/// 표시 이름은 각 피어가 같은 배열에서 로컬로 꺼낸다 (CCTVSwitcher의 카메라 배열과 같은 방식).
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class MapSelection : NetworkedManagerBase
{
    /// <summary>고를 수 있는 맵 한 장. 새 맵을 추가할 때의 씬 요건은 docs/578-map-select.md 참고.</summary>
    [Serializable]
    public class Entry
    {
        [Tooltip(
            "로드할 씬 이름. EditorBuildSettings에 등록돼 있어야 한다 — NGO 씬 동기화가 이름으로 로드한다"
        )]
        public string SceneName;

        [Tooltip("콘솔에 표시할 이름. 비우면 씬 이름을 그대로 쓴다")]
        public LocalizedString DisplayName;

        [Tooltip(
            "콘솔에 띄울 항공뷰 이미지. 에디터에서 맵 위에 직교 카메라를 놓고 한 번 렌더해 둔 스프라이트다"
                + " — 런타임 생성이 아니다. 비우면 이미지 없이 이름만 뜬다"
        )]
        public Sprite Preview;

        [Tooltip(
            "이 맵에 스폰될 NPC 수. 손으로 적는 값이다 — 고르는 시점(Shop)에는 맵 씬이 로드돼 있지 않아"
                + " 스포너에서 읽을 수 없다. 맵 씬의 NpcSpawner.m_spawnCount를 바꾸면 여기도 함께 고칠 것"
        )]
        [Min(0)]
        public int NpcCount;

        [Tooltip(
            "미리보기를 90도 눕혀서 보여준다 — 세로로 긴 맵을 가로 프레임에 크게 담을 때 켠다."
                + " 본부 미니맵도 같은 맵을 눕혀 띄우고 있어야 두 화면의 방향이 어긋나지 않는다"
        )]
        public bool PreviewRotated;
    }

    [Tooltip("고를 수 있는 맵 목록 — 순서가 곧 콘솔의 이전/다음 순서다. 첫 칸이 기본 선택")]
    [SerializeField]
    private Entry[] m_maps;

    private readonly NetworkVariable<int> m_selectedIndex = new(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    public int MapCount => m_maps != null ? m_maps.Length : 0;
    public int SelectedIndex => m_selectedIndex.Value;

    /// <summary>선택이 바뀌었다 — 콘솔 표시가 구독한다. 전 피어에서 발생한다.</summary>
    public event Action OnSelectionChanged;

    /// <summary>
    /// 선택된 맵의 씬 이름 — 목록이 비었거나 칸이 미배선이면 <c>null</c>.
    /// null을 돌려주는 건 호출부(AppHelper)가 기본 맵으로 떨어지게 하기 위함이다.
    /// </summary>
    public string SelectedSceneName => NullIfBlank(Selected?.SceneName);

    /// <summary>
    /// 선택된 맵의 표시 이름 — 비워 뒀으면 씬 이름으로 대신한다. 콘솔 표시용.
    /// 읽는 피어의 언어로 푼다 — 이 값은 복제되지 않고 각 피어가 자기 목록에서 꺼내므로,
    /// 호스트와 클라의 언어가 갈려도 각자 자기 언어로 본다 (#497 관례).
    /// </summary>
    public string SelectedDisplayName
    {
        get
        {
            LocalizedString name = Selected?.DisplayName;
            return name != null && !name.IsEmpty
                ? NullIfBlank(name.GetLocalizedString()) ?? SelectedSceneName
                : SelectedSceneName;
        }
    }

    /// <summary>선택된 맵의 항공뷰 이미지 — 배선하지 않았으면 <c>null</c>. 콘솔은 이름만 표시로 떨어진다. (#611)</summary>
    public Sprite SelectedPreview => Selected?.Preview;

    /// <summary>선택된 맵에 스폰될 NPC 수 — 칸이 비어 있으면 0. (#611)</summary>
    public int SelectedNpcCount => Selected?.NpcCount ?? 0;

    /// <summary>선택된 맵의 미리보기를 눕혀 그릴지 — 세로로 긴 맵용. (#611)</summary>
    public bool SelectedPreviewRotated => Selected?.PreviewRotated ?? false;

    // 인덱스를 받는 조회는 두지 않는다 — 이 콘솔은 목록이 아니라 고른 한 장만 보여준다.
    // 전체 목록을 찍게 되면 그때 RemoteDoorConsole.GetDoor(int) 같은 걸 다시 열면 된다.
    private Entry Selected =>
        m_maps != null && SelectedIndex >= 0 && SelectedIndex < m_maps.Length
            ? m_maps[SelectedIndex]
            : null;

    private static string NullIfBlank(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    public override void OnNetworkSpawn()
    {
        // 이어하기로 시작했으면 지난번에 골라 둔 맵이 그대로 선택된 채로 시작한다 (#373).
        // 맵 목록이 줄어든 뒤의 세이브일 수 있으므로 범위를 벗어나면 첫 칸으로 떨어뜨린다.
        if (IsServer && SaveService.Pending != null)
        {
            int saved = SaveService.Pending.MapIndex;
            m_selectedIndex.Value = saved >= 0 && saved < MapCount ? saved : 0;
        }

        m_selectedIndex.OnValueChanged += HandleSelectedIndexChanged;
        OnSelectionChanged?.Invoke();
    }

    public override void OnNetworkDespawn()
    {
        m_selectedIndex.OnValueChanged -= HandleSelectedIndexChanged;
    }

    private void HandleSelectedIndexChanged(int previous, int current) =>
        OnSelectionChanged?.Invoke();

    /// <summary>목록 이동 요청 — 이전/다음 버튼이 부른다. delta는 -1 또는 +1.</summary>
    [Rpc(SendTo.Server)]
    public void RequestSelectRpc(int delta)
    {
        int count = MapCount;
        if (count == 0 || delta == 0)
            return;

        // 버튼의 CanInteract만으로는 부족하다 — 그건 조준 피드백용 클라이언트 게이팅이라
        // RPC를 직접 부르면 뚫린다. 잠그는 이유는 IsSelectable 쪽에 적었다.
        if (!IsSelectable)
            return;

        // C# %는 음수를 그대로 돌려준다 — 두 번 감아 양수로 만든다 (CCTVSwitcher.RequestSwitchRpc와 동일)
        m_selectedIndex.Value = ((m_selectedIndex.Value + delta) % count + count) % count;
    }

    /// <summary>
    /// 지금 맵을 고를 수 있는가 — Shop에 있고 아직 출동하지 않았을 때만. 버튼의 조준 피드백 판정과
    /// RPC의 서버 가드가 같은 기준을 본다. 출동한 뒤에 바뀌면 실제로 로드되는 맵과 화면에 뜬 맵이 어긋난다.
    ///
    /// 잠금을 별도 동기화 변수로 두지 않는 이유: Shop을 벗어나면 App.SceneFlow.Shop이 null이 되고,
    /// 라운드가 끝나 Shop으로 돌아오면 ShopManager가 새 인스턴스로 뜬다 — 잠금과 해제가 씬 수명에서
    /// 저절로 따라온다. 클라에서는 ShopManager.IsDispatched가 항상 false지만(서버만 세우는 값)
    /// 그쪽 판정은 피드백용이고 권위는 이 함수를 서버에서 부를 때의 값이다.
    /// </summary>
    public static bool IsSelectable =>
        App.SceneFlow.Shop != null && !App.SceneFlow.Shop.IsDispatched;
}
