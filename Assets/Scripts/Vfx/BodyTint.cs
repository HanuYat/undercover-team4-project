using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 몸 색 오버라이드의 <b>단일 소유자</b> — 렌더러의 <c>_BaseColor</c>를 덮어쓰는 유일한 경로다. (#478)
///
/// 이 부품이 필요한 이유: 같은 몸을 물들이려는 연출이 둘 이상이다.
/// 감전 발광(<see cref="NpcShockView"/>, #477)과 타격 플래시(<see cref="NpcHitView"/>, #478)가
/// 각자 <see cref="MaterialPropertyBlock"/>을 들고 쓰면 서로의 값을 덮어쓰고, 한쪽이 오버라이드를
/// 걷어내는 순간 다른 쪽 표시까지 함께 지워진다. 소유권을 여기 하나로 모아 그 사고를 없앤다.
///
/// <b>채널은 둘이고 우선순위가 있다:</b>
/// <list type="bullet">
///   <item><see cref="SetFlash"/> — 순간 표시. 맞은 그 찰나에만 쓴다.</item>
///   <item><see cref="SetSustained"/> — 지속 표시. 상태가 유지되는 동안 깔린다.</item>
/// </list>
/// 플래시가 지속보다 <b>우선</b>한다 — 손상된 몸이 다시 맞으면 그 순간은 새 타격이 보여야 한다.
///
/// <b>머티리얼 인스턴스를 만들지 않는다.</b> PropertyBlock으로만 칠하므로 누수도, 배칭 파괴도 없고,
/// 오버라이드를 걷어내면 외형 배정(<c>AppearanceAssigner</c>)이 칠한 원래 색이 그대로 돌아온다.
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

    private void Refresh()
    {
        if (m_hasFlash)
        {
            Apply(m_flash);
            return;
        }

        if (m_hasSustained)
        {
            Apply(m_sustained);
            return;
        }

        if (!m_applied)
            return; // 이미 원래 색 — 매번 빈 블록을 다시 밀어 넣을 이유가 없다

        // 흰색을 칠하는 게 아니라 오버라이드 자체를 걷어낸다. 그래야 원래 머티리얼 색이 돌아온다.
        m_block.Clear();
        PushBlock(visibleOnly: false); // 걷어낼 때는 전부 — 그 사이 꺼진 렌더러에 색이 남지 않게
        m_applied = false;
    }

    private void Apply(Color color)
    {
        m_block.Clear();
        m_block.SetColor(s_baseColorId, color);
        PushBlock(visibleOnly: true);
        m_applied = true;
    }

    /// <summary>
    /// 프로퍼티 블록을 렌더러들에 민다.
    /// </summary>
    /// <param name="visibleOnly">
    /// 칠할 때는 true — Synty 캐릭터는 바디 변형 스킨드 메시를 20개 남짓 달고 있고
    /// <see cref="NpcAppearance"/>가 그중 하나만 켜므로, 전부 칠하면 보이지도 않는 19개에
    /// <c>SetPropertyBlock</c>을 헛으로 부른다(플래시 동안 매 프레임 반복된다).
    /// 걷어낼 때는 false — 켜짐 여부가 바뀌었을 가능성이 있어 전부 훑는 편이 안전하고, 드물게 일어난다.
    /// </param>
    private void PushBlock(bool visibleOnly)
    {
        for (int i = 0; i < m_targets.Count; i++)
        {
            Renderer renderer = m_targets[i];
            if (renderer == null)
                continue;
            if (visibleOnly && (!renderer.enabled || !renderer.gameObject.activeInHierarchy))
                continue;

            renderer.SetPropertyBlock(m_block);
        }
    }
}
