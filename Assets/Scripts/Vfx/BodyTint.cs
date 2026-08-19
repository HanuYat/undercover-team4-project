using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 몸 색 오버라이드의 <b>단일 소유자</b> — 렌더러의 <c>_BaseColor</c>를 덮어쓰는 유일한 경로다. (#478)
/// 각자 MaterialPropertyBlock을 들면 서로의 값을 덮어쓰고, 한쪽이 걷을 때 다른 쪽 표시까지 지워진다.
///
/// 채널 우선순위는 flash > sustained > base. 밑색(base)은 연출이 아니라 그 몸의 평상시 색이고,
/// 부위별로 다를 수 있다 — 서브메시마다 다른 색이 들어간다 (#432).
/// 머티리얼 인스턴스는 만들지 않는다 — 블록으로만 칠하므로 누수도 배칭 파괴도 없다.
/// </summary>
public class BodyTint : MonoBehaviour
{
    private static readonly int s_baseColorId = Shader.PropertyToID("_BaseColor");

    // _BaseColor를 가진 렌더러만 담는다. 없는 프로퍼티에 SetColor를 하면 조용히 무시되지만,
    // 걸러 두면 갱신할 때마다 도는 순회가 줄어든다.
    private readonly List<Renderer> m_targets = new List<Renderer>();
    private MaterialPropertyBlock m_block;

    private Color m_flash;
    private bool m_hasFlash;
    private Color m_sustained;
    private bool m_hasSustained;
    private Color[] m_baseColors; // 인덱스 = 서브메시. 하나뿐이면 전체에 같은 색
    private Color m_baseFallback; // 서브메시가 나뉘지 않은 렌더러(1인칭 팔 등)에 쓸 색
    private bool m_hasBase;
    private bool m_applied; // 지금 오버라이드가 걸려 있는가 — 불필요한 재적용을 막는다

    private void Awake()
    {
        m_block = new MaterialPropertyBlock();

        foreach (Renderer renderer in GetComponentsInChildren<Renderer>(true))
        {
            // 파티클·트레일은 몸이 아니다 — 감전 아크(#477)가 자식으로 생기므로 사전에 걸러야
            // 나중에 붙는 연출까지 물들이지 않는다.
            if (renderer is ParticleSystemRenderer || renderer is TrailRenderer)
                continue;

            Material material = renderer.sharedMaterial;
            if (material != null && material.HasProperty(s_baseColorId))
                m_targets.Add(renderer);
        }

        // 하나도 못 찾으면 이 몸의 색 연출은 <b>통째로 무동작</b>이 된다 — 셰이더에 _BaseColor가
        // 없으면 SetColor가 조용히 무시되기 때문이다. 에러도 로그도 없이 사라지므로 여기서 알린다.
        if (!HasTargets)
            Debug.LogWarning(
                $"BodyTint: _BaseColor를 가진 렌더러가 없다 — 이 오브젝트의 색 연출은 동작하지 않는다 ({name})",
                this
            );
    }

    /// <summary>이 몸에 색을 입힐 수 있는가 — 셰이더에 <c>_BaseColor</c>가 없으면 false. (연출 생략 판단용)</summary>
    public bool HasTargets => m_targets.Count > 0;

    /// <summary>순간 표시 색을 건다. 지속 표시보다 우선한다.</summary>
    public void SetFlash(Color color)
    {
        m_flash = color;
        m_hasFlash = true;
        Refresh();
    }

    /// <summary>순간 표시를 걷는다 — 지속 표시가 걸려 있으면 그쪽이 다시 드러난다.</summary>
    public void ClearFlash()
    {
        if (!m_hasFlash)
            return;

        m_hasFlash = false;
        Refresh();
    }

    /// <summary>지속 표시 색을 건다. 플래시가 없을 때만 화면에 보인다.</summary>
    public void SetSustained(Color color)
    {
        m_sustained = color;
        m_hasSustained = true;
        Refresh();
    }

    /// <summary>지속 표시를 걷는다.</summary>
    public void ClearSustained()
    {
        if (!m_hasSustained)
            return;

        m_hasSustained = false;
        Refresh();
    }

    /// <summary>밑색을 건다 — 몸 전체 한 색. (#432)</summary>
    public void SetBase(Color color) => SetBase(new[] { color }, color);

    /// <summary>부위별 밑색을 건다 — 배열 인덱스가 서브메시 번호다. (#432)</summary>
    /// <param name="fallback">서브메시가 나뉘지 않은 렌더러에 칠할 색</param>
    public void SetBase(Color[] perSubmesh, Color fallback)
    {
        m_baseColors = perSubmesh;
        m_baseFallback = fallback;
        m_hasBase = perSubmesh != null && perSubmesh.Length > 0;
        Refresh();
    }

    /// <summary>밑색을 걷는다 — 머티리얼 원색으로 돌아간다.</summary>
    public void ClearBase()
    {
        if (!m_hasBase)
            return;

        m_hasBase = false;
        Refresh();
    }

    private void Refresh()
    {
        if (m_hasFlash)
        {
            ApplyUniform(m_flash);
            return;
        }

        if (m_hasSustained)
        {
            ApplyUniform(m_sustained);
            return;
        }

        if (m_hasBase)
        {
            ApplyBase();
            return;
        }

        if (!m_applied)
            return; // 이미 원래 색

        // 흰색을 칠하는 게 아니라 오버라이드 자체를 걷어낸다 — 그래야 머티리얼 원색이 돌아온다
        m_block.Clear();
        Push(color: null, visibleOnly: false); // 걷을 때는 전부 — 그 사이 꺼진 렌더러에 색이 남지 않게
        m_applied = false;
    }

    private void ApplyUniform(Color color)
    {
        Push(color, visibleOnly: true);
        m_applied = true;
    }

    // 서브메시별로 다른 색 — 나뉘지 않은 렌더러는 fallback 한 색으로 칠한다.
    // 꺼진 렌더러도 칠한다: 시체 모델은 평시에 꺼져 있다가 죽을 때 켜지므로(PlayerRagdoll),
    // 건너뛰면 그때 원래 색으로 나온다. 밑색은 색을 고를 때만 도는 경로라 비용이 문제되지 않는다. (#432)
    private void ApplyBase()
    {
        for (int i = 0; i < m_targets.Count; i++)
        {
            Renderer renderer = m_targets[i];
            if (renderer == null)
                continue;

            int slots = renderer.sharedMaterials.Length;
            if (slots <= 1)
            {
                SetBlock(renderer, m_baseFallback, slot: -1);
                continue;
            }

            for (int slot = 0; slot < slots; slot++)
            {
                Color color = slot < m_baseColors.Length ? m_baseColors[slot] : m_baseFallback;
                SetBlock(renderer, color, slot);
            }
        }

        m_applied = true;
    }

    /// <summary>
    /// 모든 렌더러에 같은 블록을 민다. <paramref name="color"/>가 null이면 오버라이드를 걷는다.
    /// </summary>
    /// <param name="visibleOnly">
    /// 칠할 때는 true — Synty 캐릭터는 안 보이는 바디 변형 메시를 여럿 달고 있어 헛일이 된다.
    /// 걸을 때는 false — 그 사이 꺼진 렌더러에 색이 남지 않게 전부 훑는다.
    /// </param>
    private void Push(Color? color, bool visibleOnly)
    {
        for (int i = 0; i < m_targets.Count; i++)
        {
            Renderer renderer = m_targets[i];
            if (renderer == null)
                continue;
            if (visibleOnly && (!renderer.enabled || !renderer.gameObject.activeInHierarchy))
                continue;

            int slots = renderer.sharedMaterials.Length;
            if (slots <= 1)
            {
                SetBlock(renderer, color, slot: -1);
                continue;
            }

            // 부위별로 칠한 뒤 전체 색으로 덮으려면 같은 단위로 밀어야 한다 — 서브메시 블록이 더 세다
            for (int slot = 0; slot < slots; slot++)
                SetBlock(renderer, color, slot);
        }
    }

    // slot이 -1이면 렌더러 단위 — 머티리얼이 하나뿐인 몸(NPC·1인칭 팔)은 이 경로만 탄다
    private void SetBlock(Renderer renderer, Color? color, int slot)
    {
        m_block.Clear();
        if (color.HasValue)
            m_block.SetColor(s_baseColorId, color.Value);

        if (slot < 0)
            renderer.SetPropertyBlock(m_block);
        else
            renderer.SetPropertyBlock(m_block, slot);
    }
}
