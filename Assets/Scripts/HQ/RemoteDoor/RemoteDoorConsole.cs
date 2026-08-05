using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 본부 원격 문 개방 콘솔의 상태 보유자 — 개방할 문 목록과 지금 선택된 문을 서버 권위로 동기화한다. (#489)
/// 상호작용은 이 컴포넌트가 아니라 별도 버튼(<see cref="RemoteDoorSelectButton"/>·
/// <see cref="RemoteDoorOpenButton"/>)이 담당한다 — 조준 윤곽선이 모니터 전체가 아니라
/// 눌릴 버튼에만 켜지게 하기 위함. 표시는 <see cref="RemoteDoorListView"/>가 맡는다.
/// (<see cref="CCTVSwitcher"/>와 같은 구조, #362)
///
/// <b>선택을 동기화하는 이유</b>: 모니터는 본부에 놓인 공용 물체라 그 앞에 두 명이 서 있으면
/// 같은 화면을 봐야 한다. 로컬 선택으로 두면 A가 고른 문과 B가 보는 문이 어긋난다.
///
/// 여닫는 권위는 문(<see cref="InteractableDoor"/>)에 있다 — 여기서는 선택된 문의 서버 API를
/// 부르기만 한다. 본부는 <b>잠긴 문도 연다</b>: 잠금은 "현장이 못 연다"는 뜻일 뿐이다.
/// </summary>
public class RemoteDoorConsole : NetworkBehaviour
{
    [Tooltip("이 콘솔로 여닫을 문들 — 순서가 곧 모니터 목록 순서다")]
    [SerializeField]
    private InteractableDoor[] m_doors;

    [Tooltip("한 번에 하나만 — 문을 열 때 이 콘솔의 다른 문을 모두 닫는다")]
    [SerializeField]
    private bool m_singleOpenOnly = true;

    private readonly NetworkVariable<int> m_selectedIndex = new(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    // 문마다의 미니맵 마커 — 없는 문은 null. 선택 상태를 여기로 밀어준다 (CCTVSwitcher.CacheNodes와 같은 구조)
    private DoorMinimapMarker[] m_markers;

    public int DoorCount => m_doors != null ? m_doors.Length : 0;
    public int SelectedIndex => m_selectedIndex.Value;

    /// <summary>지금 선택된 문 — 목록이 비었거나 미배선 칸이면 null.</summary>
    public InteractableDoor SelectedDoor => GetDoor(SelectedIndex);

    /// <summary>목록 index번째 문 — 범위를 벗어나거나 미배선이면 null. 모니터 표시용.</summary>
    public InteractableDoor GetDoor(int index) =>
        m_doors != null && index >= 0 && index < m_doors.Length ? m_doors[index] : null;

    /// <summary>선택 또는 문 상태가 바뀌었다 — 모니터 표시가 구독한다. 전 피어에서 발생한다.</summary>
    public event Action OnConsoleChanged;

    public override void OnNetworkSpawn()
    {
        CacheMarkers();
        m_selectedIndex.OnValueChanged += HandleSelectedIndexChanged;

        // 문 개폐도 이 이벤트로 모아 준다 — 모니터가 콘솔 하나만 구독하면 되게 한다
        for (int i = 0; i < DoorCount; i++)
        {
            if (m_doors[i] != null)
                m_doors[i].OnOpenChanged += HandleDoorOpenChanged;
        }

        Apply();
    }

    private void CacheMarkers()
    {
        int count = DoorCount;
        m_markers = new DoorMinimapMarker[count];
        for (int i = 0; i < count; i++)
        {
            if (m_doors[i] == null)
                continue;

            // InChildren — 마커가 문 루트에 있어도 잡히고, 문짝 리그 안쪽에 붙이는 배치도 허용한다
            m_markers[i] = m_doors[i].GetComponentInChildren<DoorMinimapMarker>();
        }
    }

    public override void OnNetworkDespawn()
    {
        m_selectedIndex.OnValueChanged -= HandleSelectedIndexChanged;

        for (int i = 0; i < DoorCount; i++)
        {
            if (m_doors[i] != null)
                m_doors[i].OnOpenChanged -= HandleDoorOpenChanged;
        }
    }

    private void HandleSelectedIndexChanged(int previous, int current) => Apply();

    private void HandleDoorOpenChanged(bool open) => Apply();

    // 미니맵 마커에 선택 상태를 밀어주고, 모니터 표시에 갱신을 알린다.
    // 마커를 여기서 갱신하는 이유는 문 상태만으로는 '지금 선택된 문'을 알 수 없기 때문이다 —
    // 선택은 콘솔의 정보다 (CCTVSwitcher.Apply가 CCTVNode.SetSelected를 밀어주는 것과 같다).
    private void Apply()
    {
        for (int i = 0; i < DoorCount; i++)
        {
            if (m_markers != null && i < m_markers.Length && m_markers[i] != null)
                m_markers[i].SetSelected(i == SelectedIndex);
        }

        OnConsoleChanged?.Invoke();
    }

    /// <summary>목록 이동 요청 — 이전/다음 버튼이 부른다. delta는 -1 또는 +1.</summary>
    [Rpc(SendTo.Server)]
    public void RequestSelectRpc(int delta)
    {
        int count = DoorCount;
        if (count == 0 || delta == 0)
            return;

        // C# %는 음수를 그대로 돌려준다 — 두 번 감아 양수로 만든다 (CCTVSwitcher.RequestSwitchRpc와 동일)
        m_selectedIndex.Value = ((m_selectedIndex.Value + delta) % count + count) % count;
    }

    /// <summary>선택된 문 개폐 요청 — 개방 버튼이 부른다. 잠금과 무관하게 여닫는다.</summary>
    [Rpc(SendTo.Server)]
    public void RequestToggleSelectedRpc()
    {
        InteractableDoor door = SelectedDoor;
        if (door == null)
            return;

        bool open = !door.IsOpen;

        // "한 번에 하나"는 문의 성질이 아니라 본부 원격 개방의 정책이라 여기서 처리한다 —
        // 현장이 E로 연 문(이 목록 밖의 잠기지 않은 문)은 이 규칙에 걸리지 않는다.
        if (open && m_singleOpenOnly)
            ServerCloseOthers(door);

        door.ServerSetOpen(open);
    }

    private void ServerCloseOthers(InteractableDoor except)
    {
        for (int i = 0; i < DoorCount; i++)
        {
            if (m_doors[i] == null || m_doors[i] == except)
                continue;

            m_doors[i].ServerSetOpen(false);
        }
    }
}
