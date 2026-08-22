using UnityEngine;

/// <summary>
/// 경범죄 범법자 표식 — 돌발 이벤트로 스폰된 난동꾼(동네 깡패·공연음란범 등)에 붙는 마커다. (GDD 6-4, #106)
/// <see cref="ArrestJudge"/>가 인계된 NPC에서 이 컴포넌트를 발견하면 진범/오검거 대조 대신
/// <see cref="ArrestVerdict.Misdemeanor"/>로 판정하고 <see cref="Reward"/>를 수익으로 지급한다
/// (지급은 첫 판정 한 번뿐 — 판정 시 ArrestJudge가 비운다).
///
/// 일반 시민/진범과 달리 CitizenIdentity의 IsCriminal 대조를 타지 않는다 — 난동꾼은 수사 대상(진범)이
/// 아니라 현장 즉결 처리 대상이기 때문이다. 런타임에 <see cref="SpawnedNpcEventBase"/>가 스폰 직후 부착한다
/// (plain MonoBehaviour라 NetworkBehaviour와 달리 런타임 AddComponent 가능). 판정은 서버 권위이므로
/// 이 마커도 서버(또는 오프라인)에서만 읽힌다 — 클라이언트에 복제할 필요가 없다.
///
/// 소란 행동(<see cref="RiotBehavior"/>)도 기록한다 — 탈옥으로 방출되면 조용한 시민으로 남는 대신
/// 원래 이벤트 행동(난동자=저항 난동, 난동꾼=도주 소란)을 재개하기 위함이다(#310 후속,
/// <see cref="MisdemeanorLoiterer.BeginRiot"/>). 침입자처럼 재개할 소란이 없는 개체는 기록하지 않는다.
/// </summary>
public class MisdemeanorOffender : MonoBehaviour
{
    /// <summary>제압·연행·판정 성공 시 팀 자금에 더할 경범죄 수익. 스폰한 이벤트가 설정한다.</summary>
    public int Reward { get; set; }

    /// <summary>탈옥 방출 시 재개할 소란 행동 — <see cref="HasRiotBehavior"/>가 참일 때만 유효.</summary>
    public ERiotBehavior RiotBehavior { get; private set; }

    /// <summary>재개할 소란 시간(초) — 스폰 이벤트의 소란 지속 시간을 그대로 물려받는다.
    /// <b>0 이하면 무제한</b>(공연음란범) — 이벤트 쪽 수명 규칙과 같은 약속이다 (#106).</summary>
    public float RiotSeconds { get; private set; }

    /// <summary>방출 시 소란을 재개하는 개체인가 — 난동자·난동꾼은 참, 침입자는 거짓.</summary>
    public bool HasRiotBehavior { get; private set; }

    /// <summary>소란 행동 기록 — 스폰한 이벤트(<see cref="SpawnedNpcEventBase"/>)가 스폰 직후 1회 호출한다.</summary>
    public void SetRiotBehavior(ERiotBehavior behavior, float seconds)
    {
        RiotBehavior = behavior;
        RiotSeconds = seconds;
        HasRiotBehavior = true;
    }
}

/// <summary>
/// 탈옥으로 방출된 경범죄자가 재개할 소란 행동. (#310)
/// 스폰 이벤트의 <b>종류</b>가 아니라 <b>재개할 동작</b>이다 — 종류마다 컴포넌트가 따로 있어도(#303 분리)
/// 재개는 "그 자리에서 저항" · "달아나며 소란" · "그냥 계속 뛴다" 중 하나로 수렴한다. 소매치기는 도주로 재개한다:
/// 수감되려면 제압을 거쳤으니 훔친 물건은 이미 떨궈진 뒤고, 빈손으로 다시 노리게 두면 같은 사람이
/// 몇 번이고 털린다.
///
/// 마커에 실려 저장되는 값이라 <b>새 행동은 뒤에 추가한다</b> — 순서를 바꾸면 기존 값의 뜻이 달라진다.
/// </summary>
public enum ERiotBehavior
{
    /// <summary>표적을 쫓아가 때리며 소란 — 동네 깡패(<see cref="StreetThugEvent"/>). (#806)</summary>
    Resist,

    /// <summary>플레이어에게서 달아나며 소란 — 소매치기(<see cref="PickpocketEvent"/>).</summary>
    Flee,

    /// <summary>위협과 무관하게 도심을 계속 뛴다 — 공연음란범(<see cref="StreakerEvent"/>). (#106)
    /// 다른 둘과 달리 <b>가까이 온 플레이어가 필요 없다</b> — 방출되면 즉시 다시 뛴다.</summary>
    Sprint,
}
