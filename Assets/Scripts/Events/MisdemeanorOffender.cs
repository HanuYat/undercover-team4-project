using UnityEngine;

/// <summary>
/// 경범죄 범법자 표식 — 돌발 이벤트로 스폰된 난동꾼(거리 난동자·나체 난동꾼 등)에 붙는 마커다. (GDD 6-4, #106)
/// <see cref="ArrestJudge"/>가 인계된 NPC에서 이 컴포넌트를 발견하면 진범/오검거 대조 대신
/// <see cref="ArrestVerdict.Misdemeanor"/>로 판정하고 <see cref="Reward"/>를 수익으로 지급한다.
///
/// 일반 시민/진범과 달리 CitizenIdentity의 IsCriminal 대조를 타지 않는다 — 난동꾼은 수사 대상(진범)이
/// 아니라 현장 즉결 처리 대상이기 때문이다. 런타임에 <see cref="SpawnedNpcEvent"/>가 스폰 직후 부착한다
/// (plain MonoBehaviour라 NetworkBehaviour와 달리 런타임 AddComponent 가능). 판정은 서버 권위이므로
/// 이 마커도 서버(또는 오프라인)에서만 읽힌다 — 클라이언트에 복제할 필요가 없다.
/// </summary>
public class MisdemeanorOffender : MonoBehaviour
{
    /// <summary>제압·연행·판정 성공 시 팀 자금에 더할 경범죄 수익. 스폰한 이벤트가 설정한다.</summary>
    public int Reward { get; set; }

    /// <summary>판정 후 신병을 유치장에 수용할지 — 탈옥 침입자는 수감(true), 스폰형 이벤트 난동꾼은
    /// 임시 거처로 걸어가 소멸하므로 수용하지 않는다(false 기본값). 스폰한 이벤트가 설정한다. (#291 B)</summary>
    public bool DetainInJail { get; set; }
}
