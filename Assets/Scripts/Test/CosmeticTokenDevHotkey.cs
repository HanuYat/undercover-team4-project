using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 치장 뽑기 토큰 개발자 단축키 (#818 D) — <b>에디터 전용</b>. P를 누르면 토큰이 쌓인다.
///
/// 정상 경로는 라운드 클리어 지급이라(<c>SettlementController.GrantCosmeticToken</c>) 자판기 하나를
/// 시험하려고 한 판을 다 돌려야 한다. 그 대기를 없애는 것이 전부다.
///
/// <b>순수 로컬이다</b> — 토큰은 계정 소유고 지급도 로컬 권위라(<see cref="CosmeticGachaMachine"/>
/// 클래스 주석) 서버에 물을 것이 없다. MPPM 클론에서 눌러도 그 클론의 계정에만 쌓인다.
/// 로그인 전이면 'local' 자리에 쌓이고, 로그인하면 계정 값이 정본이라 덮인다.
///
/// 관례는 <see cref="SuddenEventDevHotkeys"/>와 같다 — <c>UNITY_EDITOR</c>로 감싸 빌드에서 사라지고,
/// 키보드가 없는 구성에서는 조용히 넘어간다.
/// </summary>
public class CosmeticTokenDevHotkey : MonoBehaviour
{
#if UNITY_EDITOR
    [Tooltip("끄면 단축키가 듣지 않는다 — 같은 키를 쓰는 다른 테스트를 할 때 잠깐 내린다")]
    [SerializeField] private bool m_enabled = true;

    [Tooltip("한 번 누를 때 받는 토큰 수")]
    [SerializeField] private int m_amount = 1;

    private void Update()
    {
        if (!m_enabled)
            return;

        // 키보드가 없는 구성(원격 데스크톱 등)에서는 조용히 넘어간다
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null || !keyboard[Key.P].wasPressedThisFrame)
            return;

        CosmeticInventory.AddTokens(m_amount);
        Debug.Log($"[치장/개발용] P — 뽑기 토큰 +{m_amount} (보유 {CosmeticInventory.Tokens}개)", this);
    }
#endif
}
