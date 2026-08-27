using UnityEngine;

/// <summary>
/// 정해 둔 씬에서만 딸린 것을 켠다 (#850) — 씬마다 HUD를 따로 만들지 않기 위한 스위치다.
/// 가릴 일이 없는 자리(커스터마이징 창)에서는 <b>이 컴포넌트를 꺼 두면</b> 만들어 둔 대로 남는다.
///
/// 자기 GameObject가 아니라 <see cref="m_content"/>를 껐다 켜는 이유는, 자기를 끄면 다시 켤 사람이
/// 없어서다. 구독을 <c>Start</c>에서 붙이는 것도 매니저 등록이 끝난 뒤라야 하기 때문이다(R6) —
/// 붙기 전에 이미 들어와 있는 씬이 있으므로 현재 씬을 한 번 반영하고 시작한다(<c>BgmPlayer</c>와 같은 자리).
/// </summary>
public class SceneScopedVisibility : MonoBehaviour
{
    [Tooltip("이 씬일 때만 켠다")]
    [SerializeField]
    private EScene m_visibleIn = EScene.Shop;

    [Tooltip("껐다 켤 대상 — 비워 두면 아무것도 하지 않는다")]
    [SerializeField]
    private GameObject m_content;

    private void Start()
    {
        if (m_content == null)
        {
            Debug.LogWarning($"[{nameof(SceneScopedVisibility)}] 껐다 켤 대상이 연결되지 않았습니다 (#850)", this);
            enabled = false;
            return;
        }

        App.OnSceneLoaded += Apply;
        Apply(App.CurrentScene);
    }

    private void OnDestroy() => App.OnSceneLoaded -= Apply;

    private void Apply(EScene scene)
    {
        if (m_content != null)
            m_content.SetActive(scene == m_visibleIn);
    }
}
