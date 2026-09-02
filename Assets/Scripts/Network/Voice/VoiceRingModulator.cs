using System;
using UnityEngine;

/// <summary>
/// 링 모듈레이터 — 음성 신호에 사인파를 곱해 기계음(로봇 목소리)을 만든다. (#372)
///
/// 원리는 곱셈 하나다. 신호에 반송파를 곱하면 원래 주파수의 합·차 성분만 남고 원음이 사라져,
/// 사람 목소리의 배음 구조가 통째로 다른 위치로 옮겨진다 — 그래서 "기계가 말하는" 소리가 된다.
/// 반송파가 낮으면(30~80Hz) 전형적인 로봇 음성, 높이면 금속성 링잉에 가까워진다.
///
/// <b>왜 직접 구현하나</b> — Unity 내장 오디오 필터(LowPass·HighPass·Echo·Distortion·Reverb·Chorus)에는
/// 링 모듈레이션이 없다. 반면 <see cref="OnAudioFilterRead"/>는 AudioSource가 붙은 오브젝트라면
/// 어디서든 샘플 버퍼를 직접 만질 수 있어, 이 정도 효과는 곱셈 한 줄로 끝난다.
/// <see cref="VivoxManager"/>가 먹통 음성 왜곡 시 오디오 탭의 AudioSource에 이 컴포넌트를 얹는다.
///
/// <b>스레드 주의</b> — OnAudioFilterRead는 오디오 스레드에서 호출된다. 여기서 Unity API를 부르면
/// 안 되므로, 샘플레이트는 Awake(메인 스레드)에서 미리 읽어 두고 설정값은 필드로만 주고받는다.
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class VoiceRingModulator : MonoBehaviour
{
    private const double k_twoPi = 2d * Math.PI;

    // 반송파 주파수(Hz). 오디오 스레드가 읽으므로 메인 스레드에서 Configure로만 바꾼다.
    private float m_carrierHz = 50f;

    // 위상은 double로 누적한다 — float면 장시간 재생에서 정밀도가 떨어져 음이 흔들린다.
    private double m_phase;
    private double m_sampleRate = 48000d;

    private void Awake()
    {
        // 오디오 스레드에서는 Unity API를 못 부르므로 여기서 미리 확보한다
        m_sampleRate = AudioSettings.outputSampleRate;
        if (m_sampleRate <= 0d)
            m_sampleRate = 48000d; // 드물게 0이 오는 구성 방어 (0이면 0 나눗셈으로 무음이 된다)
    }

    /// <summary>반송파 주파수를 설정한다 — 메인 스레드에서만 호출할 것.</summary>
    public void Configure(float carrierHz)
    {
        m_carrierHz = Mathf.Max(1f, carrierHz);
    }

    private void OnAudioFilterRead(float[] data, int channels)
    {
        if (channels <= 0)
            return;

        double step = k_twoPi * m_carrierHz / m_sampleRate;

        for (int i = 0; i + channels <= data.Length; i += channels)
        {
            // 한 프레임(모든 채널)에 같은 반송파 값을 곱한다 — 채널마다 다르면 좌우가 어긋나 들린다
            float modulator = (float)Math.Sin(m_phase);
            for (int c = 0; c < channels; c++)
                data[i + c] *= modulator;

            m_phase += step;
        }

        // 위상이 무한정 커지면 double이라도 결국 정밀도를 잃는다 — 한 바퀴로 감아 둔다
        m_phase %= k_twoPi;
    }
}
