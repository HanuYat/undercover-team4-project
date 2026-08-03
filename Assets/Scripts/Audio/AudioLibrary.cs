using System;
using UnityEngine;

/// <summary>
/// 효과음 카탈로그 — <see cref="EAudioClip"/> → 클립·볼륨·감쇠 거리. (#478, #483 선취)
/// <see cref="SoundManager"/>가 유일한 소비자다.
///
/// <b>감쇠 거리를 항목마다 두는 이유</b> — 소리마다 들려야 할 범위가 다르다. 진압봉 스윙음은
/// 바로 옆에서만 들리면 되지만 타격음은 조금 더 멀리 가야 "저기서 누가 치고 있다"가 읽힌다.
/// 매니저에 고정값을 박으면 이 차이를 만들 수 없다.
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

    [Tooltip("효과음 목록. 같은 Id가 둘 이상이면 먼저 오는 것만 쓰이고 매니저가 경고한다")]
    [SerializeField] private Entry[] m_entries;

    public Entry[] Entries => m_entries ?? Array.Empty<Entry>();
}
