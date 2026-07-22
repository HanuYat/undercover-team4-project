using UnityEngine;

/// <summary>
/// 폭탄 선 시각화 — 선(<see cref="BombWire"/>)에 붙어 퍼즐이 정한 색을 입히고 절단 상태를 보여준다. (#232 표현 계층)
///
/// 순수 표현이다(서버 권위 아님). 색·절단은 전 피어가 <see cref="BombDevice"/>의 동기화 상태에서 읽는다:
/// 색은 <see cref="BombDevice.Puzzle"/>(시드로 재구성 — 전 피어 동일), 절단은 <see cref="BombDevice.IsWireCut"/>(동기화 마스크).
/// 그래서 이 뷰는 판정에 관여하지 않고 <see cref="BombDevice.OnPuzzleReady"/>·<see cref="BombDevice.OnCutStateChanged"/>만 구독한다.
///
/// 잘린 선은 색만 어두워지는 게 아니라 <b>실제로 끊어져 보여야 한다</b> — 어느 선을 이미 잘랐는지가
/// 현장·본부의 다음 판단 근거이고, 색 변화만으로는 원래 검정 선과 구분되지 않는다. 그래서 절단 시
/// 선 몸통을 숨기고 양 끝에 토막(스텁) 둘만 남겨 가운데를 비운다 (아래 EnsureStubs).
///
/// 색 매핑(enum→RGB)은 여기 표현 계층에 둔다 — <see cref="BombPuzzle"/>은 UnityEngine에 의존하지 않는 순수 로직이라
/// 색 이름(<see cref="BombPuzzle.DescribeColor"/>)만 갖고, 실제 렌더 색은 뷰가 정한다.
/// </summary>
[RequireComponent(typeof(BombWire))]
public class BombWireView : MonoBehaviour
{
    [Tooltip("색을 입힐 선 렌더러 — 비우면 이 오브젝트의 Renderer를 쓴다")]
    [SerializeField]
    private Renderer m_renderer;

    [Tooltip("잘린 선이 어두워지는 정도(0=원색, 1=검정)")]
    [Range(0f, 1f)]
    [SerializeField]
    private float m_cutDim = 0.7f;

    [Tooltip("잘린 뒤 양 끝에 남는 토막의 길이 비율(선 길이 대비) — 나머지가 끊긴 틈이 된다")]
    [Range(0.05f, 0.49f)]
    [SerializeField]
    private float m_cutStubRatio = 0.36f;

    private BombWire m_wire;
    private BombDevice m_device;
    private MaterialPropertyBlock m_mpb;

    // 절단 표현용 토막 둘 — 선 렌더러의 자식이라 선의 굵기·방향(로컬 스케일)을 그대로 물려받는다.
    // 원본 메시가 없으면(스킨드 메시 등) 만들지 않고, 그 경우 절단은 "선이 사라짐"으로 표현된다.
    private Renderer[] m_cutStubs;
    private bool m_stubsBuilt;

    // URP Lit의 색 프로퍼티. material.color 대신 MaterialPropertyBlock을 써서 머티리얼 인스턴스 누수를 막는다.
    private static readonly int k_baseColorId = Shader.PropertyToID("_BaseColor");

    private void Awake()
    {
        m_wire = GetComponent<BombWire>();
        if (m_renderer == null)
            m_renderer = GetComponent<Renderer>();
        m_mpb = new MaterialPropertyBlock();
    }

    private void OnEnable()
    {
        // 선은 폭탄 루트의 자식이므로 부모에서 장치를 찾는다.
        m_device = GetComponentInParent<BombDevice>();
        if (m_device != null)
        {
            m_device.OnPuzzleReady += Refresh;
            m_device.OnCutStateChanged += Refresh;
        }
        Refresh(); // 이미 무장한 폭탄(늦은 활성/재접속)이면 현재 상태를 즉시 반영
    }

    private void OnDisable()
    {
        if (m_device != null)
        {
            m_device.OnPuzzleReady -= Refresh;
            m_device.OnCutStateChanged -= Refresh;
        }
    }

    private void Refresh()
    {
        if (m_renderer == null || m_device == null || m_device.Puzzle == null || m_wire.Index < 0)
            return;

        bool cut = m_device.IsWireCut(m_wire.Index);
        Color color = ToColor(m_wire.Color);
        if (cut)
            color = Color.Lerp(color, Color.black, m_cutDim); // 잘린 토막은 어둡게

        if (cut)
            EnsureStubs();

        // 끊긴 선은 몸통을 숨기고 양 끝 토막만 남긴다 — 가운데가 비어 "잘렸다"가 한눈에 보인다
        m_renderer.enabled = !cut;
        ApplyColor(m_renderer, color);

        if (m_cutStubs != null)
        {
            for (int i = 0; i < m_cutStubs.Length; i++)
            {
                m_cutStubs[i].gameObject.SetActive(cut);
                ApplyColor(m_cutStubs[i], color);
            }
        }
    }

    private void ApplyColor(Renderer target, Color color)
    {
        if (target == null)
            return;
        target.GetPropertyBlock(m_mpb);
        m_mpb.SetColor(k_baseColorId, color);
        target.SetPropertyBlock(m_mpb);
    }

    // 절단 토막을 한 번만 만든다(첫 절단 시점 — 안 잘린 선은 비용이 0).
    // 선 메시는 로컬 X를 긴 축으로 하는 큐브라, 렌더러의 자식으로 두면 로컬 좌표 -0.5~0.5가 곧 선의 양 끝이다.
    private void EnsureStubs()
    {
        if (m_stubsBuilt)
            return;
        m_stubsBuilt = true; // 메시가 없어 못 만드는 경우도 재시도하지 않는다

        MeshFilter source = m_renderer.GetComponent<MeshFilter>();
        if (source == null || source.sharedMesh == null)
            return; // 큐브류가 아니면 토막을 만들 수 없다 — 절단은 "사라짐"으로 표현된다

        m_cutStubs = new Renderer[2];
        for (int i = 0; i < 2; i++)
        {
            float sign = i == 0 ? -1f : 1f;

            GameObject stub = new GameObject("CutStub" + i);
            stub.transform.SetParent(m_renderer.transform, false);
            stub.transform.localScale = new Vector3(m_cutStubRatio, 1f, 1f);
            // 토막 중심을 각 끝 쪽으로 밀어 바깥 끝을 원래 선 끝에 맞춘다
            stub.transform.localPosition = new Vector3(sign * (0.5f - m_cutStubRatio * 0.5f), 0f, 0f);

            stub.AddComponent<MeshFilter>().sharedMesh = source.sharedMesh;
            MeshRenderer stubRenderer = stub.AddComponent<MeshRenderer>();
            stubRenderer.sharedMaterials = m_renderer.sharedMaterials;
            stubRenderer.shadowCastingMode = m_renderer.shadowCastingMode;

            stub.SetActive(false);
            m_cutStubs[i] = stubRenderer;
        }
    }

    /// <summary>선 색 enum → 렌더 색. 표현 계층의 색 정의(단일 지점).</summary>
    public static Color ToColor(BombWireColor wireColor)
    {
        switch (wireColor)
        {
            case BombWireColor.Red:
                return new Color(0.85f, 0.12f, 0.12f);
            case BombWireColor.Blue:
                return new Color(0.15f, 0.38f, 0.9f);
            case BombWireColor.Yellow:
                return new Color(0.95f, 0.82f, 0.15f);
            case BombWireColor.Black:
                return new Color(0.08f, 0.08f, 0.08f);
            case BombWireColor.White:
                return new Color(0.92f, 0.92f, 0.92f);
            default:
                return Color.magenta; // 정의 안 된 색 — 눈에 띄게
        }
    }
}
