using System;

/// <summary>
/// 세이브 한 벌 — 라운드 종료(성공) 시점의 판 상태. (#373)
/// 담는 것은 <b>씬을 넘어 사는 상주 홀더의 값</b>과 개인 지갑뿐이다. 그 밖(바닥에 떨어진 아이템·손에 든 것 등)은
/// 애초에 매 라운드 회수 후 재배달되므로(#370) 구매 목록만 있으면 복원된다.
///
/// JsonUtility로 직렬화해 Cloud Save에 문자열 한 칸으로 올린다 — 그래서 전부 public 필드다(프로퍼티는 안 실린다).
/// 필드를 늘리면 <see cref="k_version"/>을 올릴 것. 버전이 다른 세이브는 읽지 않고 버린다(SaveService).
/// </summary>
[Serializable]
public class SessionSaveData
{
    /// <summary>세이브 포맷 버전 — 필드 구성이 바뀌면 올린다. 다르면 읽지 않는다.</summary>
    public const int k_version = 1;

    public int Version = k_version;

    /// <summary>다음에 시작할 라운드 번호 (RoundProgress.Current).</summary>
    public int Round = RoundProgress.k_firstRound;

    /// <summary>팀 공용 자금 잔액 (TeamFund.Balance).</summary>
    public int TeamFund;

    /// <summary>다음 라운드로 갈 맵 인덱스 (MapSelection.SelectedIndex).</summary>
    public int MapIndex;

    /// <summary>구매한 소지형 — 프리팹 이름(중복 포함). 이름을 id로 쓰는 이유는 SaveItemLookup 참고.</summary>
    public string[] CarriedItems = Array.Empty<string>();

    /// <summary>구매한 설치형 — EInstallable 이름. 정수 대신 이름이라 enum 순서를 바꿔도 견딘다.</summary>
    public string[] Installables = Array.Empty<string>();

    /// <summary>이번 라운드 상점 진열 — 칸 순서대로. 비어 있으면 새로 추첨한다. (#925)
    /// 버전은 올리지 않는다 — JsonUtility가 없는 필드를 기본값으로 두므로 옛 세이브는 빈 배열이 된다.</summary>
    public ShopSlotSaveEntry[] ShopSlots = Array.Empty<ShopSlotSaveEntry>();

    /// <summary>개인 지갑 — UGS PlayerId 기준. 이번에 접속하지 않은 사람 몫도 그대로 남는다.</summary>
    public PlayerSaveEntry[] Players = Array.Empty<PlayerSaveEntry>();
}

/// <summary>개인 지갑 한 칸 — 키는 UGS PlayerId다. 세션이 바뀌면 clientId는 재발급되므로 쓸 수 없다.</summary>
// TODO: 개인 자금으로 사는 아이템이 들어오면 여기에 그 개인 소지품 목록을 추가한다.
/// <summary>
/// 상점 진열 칸 하나 (#925). 카탈로그 인덱스가 아니라 품목 id로 적는다 — 인덱스는 카탈로그 순서가
/// 바뀌면 뜻이 달라지고, 저장은 이름으로 한다는 관례가 이미 있다(SaveItemLookup).
/// </summary>
[Serializable]
public class ShopSlotSaveEntry
{
    /// <summary>소지형이면 프리팹 이름, 설치형이면 EInstallable 이름. 빈 문자열 = 빈 칸.</summary>
    public string Id = string.Empty;

    /// <summary>id를 어느 이름 공간으로 읽을지 — 둘이 겹칠 수 있어 함께 적는다.</summary>
    public bool Installable;

    /// <summary>EShopSlotStatus 값.</summary>
    public int Status;
}

// 팀 구매(SessionSaveData.CarriedItems)와 달리 소유자가 갈리므로 팀 목록에 합치지 말고 이 엔트리에 붙일 것 —
// 프리팹 이름 배열로 담는 방식은 팀 쪽과 같다(SaveItemLookup). 추가하면 SessionSaveData.k_version을 올린다.
[Serializable]
public class PlayerSaveEntry
{
    public string PlayerId;
    public int Balance;
}
