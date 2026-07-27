using UnityEngine;

/// <summary>
/// 수갑 아이템 — 검거·연행 기능이 밧줄(Rope)로 완전 이관되어 폐기됐다. (#369)
/// 시작 지급 로드아웃(Player.prefab)에서 빠졌고 사용 진입점도 없다 — 들고 있어도 아무 동작 안 한다.
/// 타입·프리팹 최종 처분과 수갑 자원 순환(#229) API 정리는 Phase 4(#369)에서 다룬다.
/// (지금은 PlayerLoadout·NpcController가 아직 이 타입을 참조하므로 클래스만 남겨 컴파일을 유지한다.)
/// </summary>
public class Handcuffs : ItemBase
{
    public override bool CanUse() => false; // 폐기 — 사용 불가 (#369)

    public override void Use(GameObject target) { } // 검거 진입점 제거 — 밧줄로 대체 (#369)
}
