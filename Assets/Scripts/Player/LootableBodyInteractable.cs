using UnityEngine;

/// <summary>
/// 쓰러진 동료 몸의 약탈 가능 판정 — R 입력이 조준 대상을 확정하는 데 쓴다. (#487, #725)
///
/// <b>더 이상 IInteractable이 아니다.</b> #725에서 일으키기(E)가 다운 유예에 다시 배선되며
/// "E는 일으키기, R은 뒤지기"로 조작이 갈렸다 — E의 일반 상호작용 경로(<see cref="PlayerInteractor"/>)를
/// 그대로 타면 다운된 동료를 겨눈 E가 일으키기와 뒤지기를 동시에 쏜다. 그래서 <see cref="PlayerLooter"/>가
/// R 입력에서 이 컴포넌트를 직접 찾아 부른다(<see cref="PlayerReviver.FindAllyTarget"/>과 같은 패턴).
///
/// 플레이어 <b>루트</b>에 붙인다 — 조준 히트박스(자식, Interactable 레이어)는 쓰러져 있는 동안만
/// 켜지고(<see cref="PlayerIncapacitation"/>), <see cref="PlayerInteractor"/>는 대상을
/// <c>GetComponentInParent</c>로 찾으므로 루트에 있어도 잡힌다.
/// </summary>
[RequireComponent(typeof(PlayerLootable))]
public class LootableBodyInteractable : MonoBehaviour
{
    private PlayerLootable m_body;

    /// <summary>이 몸의 <see cref="PlayerLootable"/> — 확정 후 <see cref="PlayerLooter.RequestOpenLoot"/>에 넘긴다.</summary>
    public PlayerLootable Body => m_body;

    private void Awake()
    {
        m_body = GetComponent<PlayerLootable>();
    }

    /// <summary>R이 실제로 동작하는 상태인지 — 약탈 가능 상태와 자기 자신 제외를 함께 본다. 서버 가드와 같은 기준 (#184).</summary>
    public bool CanLoot(GameObject looterObject)
    {
        if (!m_body.CanBeLooted)
            return false;

        PlayerLooter looter =
            looterObject != null ? looterObject.GetComponentInParent<PlayerLooter>() : null;
        return looter != null && looter.gameObject != gameObject;
    }
}
