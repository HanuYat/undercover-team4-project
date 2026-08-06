using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 감정표현 1종의 정의 — 무엇이 재생되고 무엇이 머리 위에 뜨는가. (#219)
///
/// <b>종류 enum을 두지 않았다.</b> "댄스" / "이모지만" / "춤추면서 이모지" 세 경우가
/// <see cref="Clip"/>·<see cref="BubbleSprite"/>의 <b>있음/없음 조합</b>으로 자연히 나오므로
/// 분기 코드가 생기지 않는다. enum을 두면 조합마다 값이 필요하고, 새 조합이 생길 때마다
/// 소비자 쪽 switch를 전부 찾아 고쳐야 한다.
///
/// 길이 필드를 따로 두지 않는 것도 같은 이유다 — 클립이 이미 길이를 알고 있는데 값을 복사해
/// 두면 클립을 갈아끼웠을 때 한쪽만 남아 조용히 어긋난다.
/// </summary>
[CreateAssetMenu(fileName = "Emote", menuName = "Scriptable Objects/Emote Definition")]
public class EmoteDefinition : ScriptableObject
{
    [Tooltip("안정적 키 — 로비 구성 저장에 쓴다. 영숫자·밑줄만. '|'는 저장 구분자라 쓸 수 없다")]
    [SerializeField]
    private string m_id;

    [Tooltip("휠·로비 목록에 표시할 이름")]
    [SerializeField]
    private LocalizedString m_displayName;

    [Tooltip("휠 칸 아이콘")]
    [SerializeField]
    private Sprite m_icon;

    [Tooltip("재생할 애니메이션. 비우면 애니메이션 없이 이모지만 뜬다")]
    [SerializeField]
    private AnimationClip m_clip;

    [Tooltip("켜면 취소할 때까지 무한 반복, 끄면 클립 1회 후 자동 종료")]
    [SerializeField]
    private bool m_loop = true;

    [Tooltip("머리 위에 띄울 아이콘. 비우면 표시하지 않는다")]
    [SerializeField]
    private Sprite m_bubbleSprite;

    /// <summary>안정적 키 — 로비 구성 저장용. 네트워크에는 카탈로그 인덱스가 실린다.</summary>
    public string Id => m_id;

    public LocalizedString DisplayName => m_displayName;
    public Sprite Icon => m_icon;
    public AnimationClip Clip => m_clip;
    public bool Loop => m_loop;
    public Sprite BubbleSprite => m_bubbleSprite;

    /// <summary>
    /// 자동 종료까지의 길이(초). 루프이거나 클립이 없으면 0 — 서버가 시간으로 끊지 않는다는 뜻이다.
    /// </summary>
    public float DurationSeconds => m_loop || m_clip == null ? 0f : m_clip.length;
}
