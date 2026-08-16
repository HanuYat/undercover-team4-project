using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Shop 씬 맵 선택 콘솔의 표시 — 다음 라운드에 갈 맵의 이름·항공뷰·스폰 NPC 수를 찍는다. (#578, #611)
/// 홀더 상태를 구독만 하는 표시 전용이고 <b>전원에게 보인다</b>(고르는 건 호스트뿐이지만,
/// 어디로 출동하는지는 전원이 알아야 한다). RemoteDoorListView와 같은 구조. (#489)
///
/// 홀더는 런타임 스폰물이라 인스펙터로 못 잡는다 — App 파사드로 조회하고, 아직 스폰 전일 수 있어
/// 구독 시점을 Update에서 잡는다(LoadingScreen이 NGO SceneManager를 다시 거는 것과 같은 이유).
/// </summary>
public class MapSelectionView : MonoBehaviour
{
    [SerializeField]
    private TMP_Text m_label;

    [Tooltip("표시 형식 — {0}=맵 이름")]
    [SerializeField]
    private string m_format = "다음 출동지: {0}";

    [Header("맵 정보 (#611)")]
    [Tooltip("항공뷰 이미지가 들어갈 자리. 비워 두면 이미지 없이 이름만 뜬다")]
    [SerializeField]
    private Image m_preview;

    [Tooltip("스폰 NPC 수를 찍을 라벨. 비워 두면 숫자를 표시하지 않는다")]
    [SerializeField]
    private TMP_Text m_npcCountLabel;

    [Tooltip("NPC 수 표시 형식 — {0}=마릿수")]
    [SerializeField]
    private string m_npcCountFormat = "시민 {0}명";

    // 지금 구독 중인 홀더 — 세션마다 새로 스폰되므로 참조가 바뀐 프레임에만 다시 건다
    private MapSelection m_bound;

    // 인스펙터에 배선된 미리보기 프레임 크기 — 눕힐 때 가로/세로를 맞바꾸는 기준이다.
    // 앵커를 가운데로 잡은(늘어나지 않는) 프레임을 전제로 한다.
    private Vector2 m_previewSize;

    private void Awake()
    {
        if (m_preview != null)
            m_previewSize = m_preview.rectTransform.sizeDelta;
    }

    private void OnDisable() => Bind(null);

    private void Update()
    {
        MapSelection current = App.Game.MapSelection;
        if (!ReferenceEquals(current, m_bound))
            Bind(current);
    }

    private void Bind(MapSelection target)
    {
        if (m_bound != null)
            m_bound.OnSelectionChanged -= Refresh;

        m_bound = target;

        if (m_bound != null)
            m_bound.OnSelectionChanged += Refresh;

        Refresh();
    }

    private void Refresh()
    {
        // 세션 밖(씬 직접 Play)이거나 아직 스폰 전 — 표시할 상태가 없다. AppHelper가 기본 맵으로 간다.
        if (m_bound == null || !m_bound.IsSpawned)
        {
            SetText(m_label, string.Empty);
            SetText(m_npcCountLabel, string.Empty);
            SetPreview(null, false);
            return;
        }

        // 칸이 통째로 비어 있으면(씬 이름조차 없음) 배선 실수라 화면에 드러낸다 — RemoteDoorListView와 같다
        string name = m_bound.SelectedDisplayName;
        SetText(m_label, string.Format(m_format, name ?? "(미배선)"));
        SetText(m_npcCountLabel, string.Format(m_npcCountFormat, m_bound.SelectedNpcCount));
        SetPreview(m_bound.SelectedPreview, m_bound.SelectedPreviewRotated);
    }

    private static void SetText(TMP_Text label, string text)
    {
        if (label != null)
            label.text = text;
    }

    // 항공뷰가 없는 맵에서는 이미지를 끈다 — 직전 맵의 그림이 남는 쪽이 빈 자리보다 나쁘다.
    // 자리(레이아웃)는 남겨야 하므로 오브젝트가 아니라 Image만 끈다.
    private void SetPreview(Sprite sprite, bool rotated)
    {
        if (m_preview == null)
            return;

        m_preview.sprite = sprite;
        m_preview.enabled = sprite != null;

        // 세로로 긴 맵은 눕혀서 보여준다 — 회전을 맵마다 데이터로 두는 이유는 두 맵의 방향이
        // 반대라서다 (Apocalypse 100x180 세로 · Cyberpunk 120x80 가로).
        //
        // <b>크기도 함께 맞바꾼다.</b> Preserve Aspect는 <b>돌리기 전</b> 사각형에 맞춰 계산되므로,
        // 가로로 긴 프레임에 세로 그림을 넣으면 폭에서 먼저 걸려 작게 들어가고 그 상태로 눕는다.
        // 미리 세로 프레임으로 바꿔 두면 꽉 채운 뒤 눕어서, 화면에 차지하는 자리는 배선한 크기 그대로다.
        m_preview.rectTransform.sizeDelta = rotated
            ? new Vector2(m_previewSize.y, m_previewSize.x)
            : m_previewSize;

        m_preview.rectTransform.localRotation = rotated
            ? Quaternion.Euler(0f, 0f, 90f)
            : Quaternion.identity;
    }
}
