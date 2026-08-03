using UnityEngine;

/// <summary>
/// 1회성 월드 VFX 생성 헬퍼 — 프리팹을 맞은 자리에 낳고 수명이 다하면 스스로 치운다. (#478)
///
/// <b>순수 로컬 연출이다</b> — 네트워크 프리팹에 등록하지 않는다. 각 피어가 서버의 판정 알림을 받고
/// 자기 화면에 만든다 (<see cref="NpcDespawnGhost"/>·<see cref="ShockArcEmitter"/>와 같은 관례).
///
/// 부모를 두지 않는다 — 임팩트는 <b>맞은 순간의 자리</b>에 남는 것이라, 맞은 몸이 넉백으로 날아가거나
/// 밧줄에 끌려가도 따라가면 안 된다. 몸에 붙어 있어야 하는 감전 아크(<see cref="ShockArcEmitter"/>)와
/// 정반대의 요구다 — 그래서 둘을 한 부품으로 합치지 않았다.
/// </summary>
public static class ImpactVfx
{
    /// <summary>
    /// 임팩트 연출을 한 번 재생한다. 프리팹이 없으면 조용히 무동작 — 연출은 선택 사항이다.
    /// </summary>
    /// <param name="prefab">재생할 파티클 프리팹 (Play On Awake).</param>
    /// <param name="position">맞은 자리(월드).</param>
    /// <param name="normal">
    /// 표면 법선. 이 방향이 프리팹의 +Z가 되도록 회전시킨다 — 임팩트 링이 맞은 면에
    /// 수직으로 서고 스파크가 튕겨 나오는 쪽을 향한다. 0벡터면 회전 없이 그대로 둔다.
    /// </param>
    /// <param name="lifetime">이 시간(초) 뒤 파괴한다. 파티클 수명 + 트레일 수명보다 길게 줄 것.</param>
    public static void Play(GameObject prefab, Vector3 position, Vector3 normal, float lifetime)
    {
        if (prefab == null)
            return;

        Quaternion rotation =
            normal.sqrMagnitude > 0.0001f ? Quaternion.LookRotation(normal) : Quaternion.identity;

        GameObject instance = Object.Instantiate(prefab, position, rotation);
        Object.Destroy(instance, Mathf.Max(0.01f, lifetime));
    }
}
