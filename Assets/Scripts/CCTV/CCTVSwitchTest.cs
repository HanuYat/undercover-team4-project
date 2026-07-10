using UnityEngine;
using UnityEngine.InputSystem;

public class CCTVSwitchTest : MonoBehaviour
{
    [SerializeField]
    CCTVSwitcher m_switcher;

    void Update()
    {
        if (Keyboard.current == null)
            return;

        if (Keyboard.current.eKey.wasPressedThisFrame)
            m_switcher.RequestSwitchNextRpc();

        if (Keyboard.current.qKey.wasPressedThisFrame)
            m_switcher.RequestSwitchPrevRpc();
    }
}
