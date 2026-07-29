using UnityEngine;

/// <summary>
/// [임시] 현재 씬을 화면 우상단에 표시 — 씬 전환 후 화면에 아무것도 안 보여도 어느 씬인지 알 수 있게. (#214)
/// AppBootstrap(상주·DDOL)에 붙여 전 씬에서 항상 보이게 한다. 정식 UI 나오면 제거.
/// App.CurrentScene은 씬 로드마다 갱신되므로(App.NotifySceneLoaded) 별도 구독 없이 그려도 최신값이다.
/// </summary>
public class SceneIndicatorHud : MonoBehaviour
{
    private static string Label(EScene scene) =>
        scene switch
        {
            EScene.Title => "메인 메뉴",
            EScene.Lobby => "로비 (대기)",
            EScene.Shop => "상점 (준비)",
            EScene.Game => "게임 (라운드)",
            _ => "…", // None — 부팅/전환 순간
        };

    private void OnGUI()
    {
        const float width = 180f;
        const float height = 26f;
        Rect rect = new Rect(12f, 12f, width, height); // 우상단 (좌측 HUD들과 안 겹치게)

        Color prev = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.55f);
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = prev;

        GUIStyle style = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 14,
            fontStyle = FontStyle.Bold,
        };
        style.normal.textColor = Color.white;

        GUI.Label(rect, $"현재 씬: {Label(App.CurrentScene)}", style);
    }
}
