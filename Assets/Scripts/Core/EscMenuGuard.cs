using UnityEngine;

/// <summary>
/// ESC 진입 메뉴(일시정지·종료 확인) 억제 플래그. (#326)
///
/// 배경: 명부/폭탄 매뉴얼/신호 해석기 같은 모달은 스택에 안 올라가고 ESC를 자기 파이프라인에서 직접 읽어 닫는다.
/// 특히 IMGUI(OnGUI Event) 모달은 UIManagerBase의 Input System ESC 읽기와 프레임 타이밍이 어긋나, 모달이 닫히며
/// 입력정지가 풀린 뒤 UIManagerBase가 같은 ESC를 뒤늦게 집어 pause가 겹쳐 뜨는 레이스가 있다.
///
/// 해결: 모달은 "열려 있는 동안" 매 프레임 <see cref="BlockThisFrame"/>을 호출한다. pause는 그 프레임과 다음
/// 프레임(+1)까지 <see cref="IsBlocked"/>로 열리지 않는다 — 실행 순서(UIManagerBase가 모달보다 먼저 Update)와
/// Input System↔IMGUI 스큐를 모두 덮는다. 입력정지 여부·파이프라인과 무관하게 동작한다.
/// (모달이 정식 Canvas 패널로 승격돼 ESC 스택에 들어가면 이 가드는 불필요해진다 — #65 계열.)
/// </summary>
public static class EscMenuGuard
{
    private static int s_blockUntilFrame = -1;

    /// <summary>모달이 열려 있는 프레임마다 호출 — 이번 프레임과 다음 프레임의 ESC 진입 메뉴 오픈을 막는다.</summary>
    public static void BlockThisFrame() => s_blockUntilFrame = Time.frameCount + 1;

    /// <summary>지금 ESC 진입 메뉴 오픈이 억제 중인가.</summary>
    public static bool IsBlocked => Time.frameCount <= s_blockUntilFrame;
}
