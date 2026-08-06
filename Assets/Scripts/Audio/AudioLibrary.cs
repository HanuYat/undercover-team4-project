using System;
using UnityEngine;

/// <summary>
/// 오디오 카탈로그 — 효과음(<see cref="EAudioClip"/>)과 BGM(<see cref="EBgm"/>)의 클립·볼륨. (#478, #483)
/// <see cref="SoundManager"/>가 유일한 소비자다.
///
/// <b>감쇠 거리를 항목마다 두는 이유</b> — 소리마다 들려야 할 범위가 다르다. 진압봉 스윙음은
/// 바로 옆에서만 들리면 되지만 타격음은 조금 더 멀리 가야 "저기서 누가 치고 있다"가 읽힌다.
/// 매니저에 고정값을 박으면 이 차이를 만들 수 없다.
///
/// <b>BGM을 같은 에셋에 두는 이유</b> — 나누면 매니저에 SO 참조가 하나 더 붙고 AppBootstrap
/// 프리팹을 다시 배선해야 한다. BGM은 항목이 씬 수만큼밖에 안 되므로 표 하나를 더 얹는 편이 싸다.
/// 3D 감쇠 거리가 필요 없다는 점만 달라 항목 타입을 따로 뒀다.
/// </summary>
[CreateAssetMenu(fileName = "AudioLibrary", menuName = "Scriptable Objects/AudioLibrary")]
public class AudioLibrary : ScriptableObject
{
    [Serializable]
    public class Entry
    {
        [Tooltip("이 항목을 가리키는 키 — 코드에서 App.Sound.PlaySfxAt(Id, ...)로 부른다")]
        public EAudioClip Id;

        [Tooltip("재생할 클립. 비우면 이 소리는 조용히 무동작한다(배선만 먼저 하고 나중에 채워도 된다)")]
        public AudioClip Clip;

        [Tooltip("재생 볼륨 배율. 전역 음량(GameSettings.MasterVolume)이 위에 한 번 더 곱해진다")]
        [Range(0f, 1f)]
        public float Volume = 1f;

        [Tooltip("이 거리(m)까지는 감쇠 없이 최대 볼륨")]
        [Min(0.1f)]
        public float MinDistance = 2f;

        [Tooltip("이 거리(m) 밖에서는 들리지 않는다")]
        [Min(1f)]
        public float MaxDistance = 25f;
    }

    /// <summary>BGM 한 곡 — 3D 감쇠가 없어(2D 재생) 거리 항목이 없다.</summary>
    [Serializable]
    public class BgmEntry
    {
        [Tooltip("이 곡을 가리키는 키 — 코드에서 App.Sound.PlayBgm(Id)로 부른다")]
        public EBgm Id;

        [Tooltip("재생할 곡. 비우면 이 BGM은 조용히 무동작한다(배선만 먼저 하고 나중에 채워도 된다)")]
        public AudioClip Clip;

        [Tooltip("재생 볼륨 배율. 효과음에 묻히지 않게 보통 효과음보다 낮게 둔다")]
        [Range(0f, 1f)]
        public float Volume = 0.5f;
    }

    /// <summary>씬에 들어갈 때 자동으로 틀 곡 — 표에 없는 씬은 무음이다.</summary>
    [Serializable]
    public class SceneBgmEntry
    {
        [Tooltip("이 씬에 들어가면")]
        public EScene Scene;

        [Tooltip("이 곡을 튼다. None이면 그 씬에서는 BGM을 끈다")]
        public EBgm Bgm;
    }

    [Tooltip("효과음 목록. 같은 Id가 둘 이상이면 먼저 오는 것만 쓰이고 매니저가 경고한다")]
    [SerializeField] private Entry[] m_entries;

    [Tooltip("BGM 목록. 같은 Id가 둘 이상이면 먼저 오는 것만 쓰이고 매니저가 경고한다")]
    [SerializeField] private BgmEntry[] m_bgm;

    [Tooltip("씬별 BGM. 여기 없는 씬은 무음이다. 두 씬에 같은 곡을 적으면 그 사이 전환에서 곡이 이어진다")]
    [SerializeField] private SceneBgmEntry[] m_sceneBgm;

    public Entry[] Entries => m_entries ?? Array.Empty<Entry>();

    public BgmEntry[] BgmEntries => m_bgm ?? Array.Empty<BgmEntry>();

    public SceneBgmEntry[] SceneBgmEntries => m_sceneBgm ?? Array.Empty<SceneBgmEntry>();
}
