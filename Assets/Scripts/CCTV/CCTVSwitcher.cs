using UnityEngine;

public class CCTVSwitcher : MonoBehaviour
{
    [SerializeField] Camera[] m_cameras;
    [SerializeField] RenderTexture m_monitorRt; 
    int m_currentIndex;

    void Start() => Apply();

    public void SwitchPrev()
    {
        m_currentIndex = (m_currentIndex - 1 + m_cameras.Length) % m_cameras.Length;
        Apply();
    }

    public void SwitchNext()
    {
        m_currentIndex = (m_currentIndex + 1) % m_cameras.Length;
        Apply();
    }

    void Apply()
    {
        for (int i = 0; i < m_cameras.Length; i++)
        {
            bool active = (i == m_currentIndex);
            m_cameras[i].targetTexture = active ? m_monitorRt : null;
            m_cameras[i].enabled = active;   // 안 보이는 카메라는 렌더 안 함
        }
    }
}
