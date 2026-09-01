using UnityEngine;
using UnityEngine.UI;

// 맵 씬의 미니맵 좌표계·항공뷰 단일 출처 (#835) — 본부·휴대용 두 MinimapViewer가 같은 지점을
// 가리키려면 같은 사각형을 읽어야 한다. MinimapTarget.ActiveTargets와 같은 idiom을 쓴다.
public class MinimapArea : MonoBehaviour
{
    public static MinimapArea Current { get; private set; }

    [Header("맵이 덮는 월드 영역")]
    [SerializeField] private float m_worldCenterX;
    [SerializeField] private float m_worldCenterZ;
    [SerializeField] private float m_worldSizeX = 100f;
    [SerializeField] private float m_worldSizeZ = 100f;

    [SerializeField] private Sprite m_aerialSprite;

    public float WorldCenterX => m_worldCenterX;
    public float WorldCenterZ => m_worldCenterZ;
    public float WorldSizeX => m_worldSizeX;
    public float WorldSizeZ => m_worldSizeZ;
    public Sprite AerialSprite => m_aerialSprite;

    private void OnEnable()
    {
        if (Current != null && Current != this)
            Debug.LogWarning($"[미니맵 영역] 씬에 MinimapArea가 둘 이상 있다 — {name}이 이긴다", this);

        Current = this;
    }

    private void OnDisable()
    {
        if (Current == this)
            Current = null;
    }

    public void ApplyTo(Image mapImage, out float centerX, out float centerZ, out float sizeX, out float sizeZ)
    {
        centerX = m_worldCenterX;
        centerZ = m_worldCenterZ;
        sizeX = m_worldSizeX;
        sizeZ = m_worldSizeZ;

        if (mapImage != null && m_aerialSprite != null)
            mapImage.sprite = m_aerialSprite;
    }
}
