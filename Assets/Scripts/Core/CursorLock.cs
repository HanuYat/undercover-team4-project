using UnityEngine;

/// <summary>
/// 마우스 커서 잠금의 단일 소유자 — 커서를 푸는 UI는 요청만 넣고 빼며, Cursor 조작은 여기서만 한다. (#352)
///
/// 이전엔 각 UI가 열기 직전 <see cref="Cursor.lockState"/>를 자기 필드에 스냅샷했다 닫을 때 되돌렸다.
/// 저장 대상이 전역 하나라, 겹쳐 열린 UI가 LIFO를 벗어나 닫히면(디스폰으로 모달이 자동으로 닫히는 등)
/// 복원값이 실제와 어긋났다. 요청 수만 세면 닫는 순서와 무관해진다.
///
/// 게임플레이 중이 아니면(오너 로컬 플레이어 없음 — 타이틀·로비, 라운드 종료 디스폰 후) 항상 풀림이다.
/// 잠긴 채 씬을 넘어가면 커서를 풀어 줄 주체가 없어 로비 UI를 못 누른다. (#188)
/// </summary>
public static class CursorLock
{
    private static int s_unlockCount; // 커서 해제를 요청 중인 UI 수 (중첩 가능)
    private static bool s_gameplayActive; // 오너 로컬 플레이어가 스폰돼 있는지

    /// <summary>지금 커서가 풀려 있는가 — 시점 회전 차단 판단에 쓴다 (<see cref="PlayerMovement"/>).</summary>
    public static bool IsUnlocked => s_unlockCount > 0 || !s_gameplayActive;

    /// <summary>커서 해제를 요청한다. <see cref="PopUnlock"/>과 반드시 1:1로 맞출 것.</summary>
    public static void PushUnlock()
    {
        s_unlockCount++;
        Apply();
    }

    /// <summary>커서 해제 요청을 거둔다. 남은 요청이 없고 게임플레이 중이면 커서가 다시 잠긴다.</summary>
    public static void PopUnlock()
    {
        // 짝이 깨져도 음수로 내려가 영영 안 잠기는 일은 없게 바닥을 친다.
        if (s_unlockCount > 0)
            s_unlockCount--;

        Apply();
    }

    /// <summary>오너 로컬 플레이어의 스폰/디스폰을 알린다 — <see cref="PlayerMovement"/> 전용.</summary>
    public static void SetGameplayActive(bool active)
    {
        s_gameplayActive = active;
        Apply();
    }

    private static void Apply()
    {
        bool unlocked = IsUnlocked;
        Cursor.lockState = unlocked ? CursorLockMode.None : CursorLockMode.Locked;
        Cursor.visible = unlocked;
    }
}
