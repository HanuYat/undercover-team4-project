using System;
using UnityEngine;

/// <summary>
/// 재생 상태를 화면에 옮긴다 — 애니메이터 파라미터와 이모지 표시. 전 피어에서 돈다. (#219)
///
/// <b>PlayerAnimationDriver에 넣지 않은 이유:</b> 그 파일은 "이동·자세를 폴링해 애니메이터에
/// 반영한다"는 축이 뚜렷하고 이미 크다. 감정표현을 얹으면 두 가지를 하는 파일이 된다.
///
/// 폴링이 아니라 <b>값 변경 구독</b>인 것도 차이다 — 이동·자세는 매 프레임 값이 바뀌지만
/// 감정표현은 시작·종료 두 순간에만 바뀐다.
/// </summary>
[RequireComponent(typeof(PlayerEmote))]
public class PlayerEmoteView : MonoBehaviour
{
    private static readonly int s_emoteHash = Animator.StringToHash("Emote");
    private static readonly int s_emoteIndexHash = Animator.StringToHash("EmoteIndex");

    [Tooltip("비우면 자식에서 자동으로 찾는다")]
    [SerializeField]
    private Animator m_animator;

    private PlayerEmote m_emote;
    private PlayerLook m_look; // 오너 3인칭 시점 전환 (#219) — 오너에만 있다

    /// <summary>표시할 감정표현이 바뀌었다 — null이면 종료. 말풍선이 구독한다.</summary>
    public event Action<EmoteDefinition> OnEmoteVisualChanged;

    private void Awake()
    {
        m_emote = GetComponent<PlayerEmote>();
        m_look = GetComponent<PlayerLook>();

        if (m_animator == null)
            m_animator = GetComponentInChildren<Animator>();
    }

    private void OnEnable()
    {
        m_emote.OnActiveEmoteChanged += HandleActiveEmoteChanged;
        Apply(m_emote.ActiveEmote); // 활성화 시점의 값을 한 번 반영 — 늦게 켜져도 어긋나지 않게
    }

    private void OnDisable()
    {
        m_emote.OnActiveEmoteChanged -= HandleActiveEmoteChanged;
        Apply(PlayerEmote.k_none); // 꺼질 때 자세를 남기지 않는다
    }

    private void HandleActiveEmoteChanged(sbyte index) => Apply(index);

    private void Apply(sbyte index)
    {
        EmoteCatalog catalog = m_emote.Catalog;
        EmoteDefinition definition = catalog != null ? catalog.Get(index) : null;
        bool active = definition != null;

        if (m_animator != null)
        {
            // 인덱스를 먼저 넣는다 — Emote를 먼저 켜면 그 프레임에 이전 인덱스의 상태로 진입할 수 있다.
            m_animator.SetInteger(s_emoteIndexHash, active ? index : PlayerEmote.k_none);
            m_animator.SetBool(s_emoteHash, active && definition.Clip != null);
        }

        // 3인칭 전환은 오너에서만 의미가 있다 — 남의 카메라는 애초에 꺼져 있다.
        // PlayerLook은 오너 로컬 전용이라 비오너 인스턴스에는 컴포넌트가 있어도 동작하지 않는다.
        //
        // 1인칭 팔 숨김도 이 안에서 함께 처리된다 — 3인칭 사용처가 사망 관전(#576)까지 둘로 늘어,
        // 여기서 따로 끄면 죽으면서 감정표현이 끊기는 순간 관전이 감춘 팔을 되살린다.
        if (m_look != null)
            m_look.SetEmoteView(active);

        OnEmoteVisualChanged?.Invoke(definition);
    }
}
